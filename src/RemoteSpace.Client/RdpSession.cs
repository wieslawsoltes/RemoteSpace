using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteSpace.Core;
using RemoteSpace.Protocol;
using RemoteSpace.Graphics;
using RemoteSpace.Security;

namespace RemoteSpace.Client;

/// <summary>Single-use RDP session with one receive loop, serialized writes, cancellation and bounded packet decoding.</summary>
public sealed partial class RdpSession : IRemoteSession, IRemoteCursorSource, IRemoteSessionTraffic
{
    private readonly SessionTrafficCounter _traffic = new(SessionTrafficLayer.TcpStream);
    public SessionTrafficSnapshot Traffic => _traffic.Snapshot;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly SessionLifetime _lifetime = new();
    private readonly object _stopGate = new();
    private bool _released;
    private readonly ChannelAssembler _clipboardFragments = new();
    private readonly FastPathAssembler _fastPath = new();
    private readonly CursorDecoder _cursors = new();
    private RemoteSpace.Licensing.LicenseClient? _licensing;
    private TcpClient? _tcp;
    private Stream? _stream;
    private ConnectionOptions? _options;
    private DemandActive? _desktop;
    private ushort _user, _global, _clipboardChannel;
    private uint[]? _palette;
    private int _state;
    public SessionState State => (SessionState)Volatile.Read(ref _state);
    public event Action<SessionState>? StateChanged;
    public event Action<DesktopSize>? DesktopResized;
    public event Action<SurfaceUpdate>? SurfaceUpdated;
    public event Action<SessionDiagnostic>? Diagnostic;
    public event Action<string>? ClipboardReceived;
    public event Action<RemoteCursor>? CursorChanged;
    public event Action<ushort, ushort>? CursorPositionChanged;
    public long ReceivedBytes { get; private set; }
    public long DecodedPixels { get; private set; }
    private void SetState(SessionState state) { Volatile.Write(ref _state, (int)state); StateChanged?.Invoke(state); }
    private void Log(string stage, string message) => Diagnostic?.Invoke(new(DateTimeOffset.UtcNow, stage, message));

    private async ValueTask<byte[]> ReadMcsAsync(CancellationToken ct)
    {
        var packet = await Framing.ReadAsync(_stream!, ct);
        if (packet.IsFastPath) throw new RdpProtocolException("Unexpected fast-path packet during MCS establishment.");
        return Framing.UnwrapData(packet.Payload);
    }
    private async ValueTask WriteAsync(byte[] data, CancellationToken ct)
    {
        await _writer.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_stop.IsCancellationRequested) throw new OperationCanceledException("The session is stopping.");
            try { await (_stream ?? throw new InvalidOperationException("The transport is not connected.")).WriteAsync(data, ct); }
            catch { StopTransport(); throw; } // A partial TPKT cannot be followed by a fresh packet.
        }
        finally { _writer.Release(); }
    }
    private ValueTask WriteMcsAsync(byte[] data, CancellationToken ct) => WriteAsync(Framing.Data(data), ct);
    private ValueTask SendChannelAsync(ushort channel, byte[] data, CancellationToken ct) => WriteMcsAsync(Mcs.SendData(_user, channel, data), ct);
    private ValueTask SendShareAsync(byte[] data, CancellationToken ct) => SendChannelAsync(_global, data, ct);

    private void Surface(SurfaceUpdate update)
    {
        if (_desktop is null || (long)update.X + update.Width > _desktop.Width || (long)update.Y + update.Height > _desktop.Height)
            throw new RdpProtocolException("Update lies outside the negotiated desktop.");
        DecodedPixels += (long)update.Width * update.Height; SurfaceUpdated?.Invoke(update);
    }
    private void SlowUpdate(byte[] data)
    {
        var r = new PacketReader(data); ushort type = r.U16();
        switch (type)
        {
            case 1: BitmapDecoder.DecodeUpdates(data, Surface, _palette); break;
            case 2: Palette(r.Bytes(r.Remaining)); break;
            case 3: r.End(); break;
            case 0:
                r.U16(); int count = r.U16(); r.U16();
                if (count != 0) throw new RdpProtocolException("Server sent drawing orders despite zero advertised order support."); r.End(); break;
            default: throw new RdpProtocolException($"Unknown bitmap update type {type}.");
        }
    }
    private void Palette(ReadOnlySpan<byte> bytes)
    {
        var r = new PacketReader(bytes); r.U16(); uint count = r.U32();
        if (count is < 1 or > 256) throw new RdpProtocolException("Invalid palette size.");
        _palette = new uint[count];
        for (int i = 0; i < count; i++) { uint red = r.U8(), green = r.U8(), blue = r.U8(); _palette[i] = (red << 16) | (green << 8) | blue; }
        r.End();
    }
    private void FastUpdate(byte code, byte[] data)
    {
        switch (code)
        {
            case 0:
                if (data.Length != 2 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 0) throw new RdpProtocolException("Unnegotiated fast-path drawing orders."); break;
            case 1: BitmapDecoder.DecodeUpdates(data, Surface, _palette); break;
            case 2:
                var p = new PacketReader(data); if (p.U16() != 2) throw new RdpProtocolException("Invalid fast-path palette type."); Palette(p.Bytes(p.Remaining)); break;
            case 3: break;
            case 5: CursorChanged?.Invoke(new(0, 0, 0, 0, [], Hidden: true)); break;
            case 6: CursorChanged?.Invoke(new(0, 0, 0, 0, [], SystemDefault: true)); break;
            case 8: Position(data); break;
            case 9: CursorChanged?.Invoke(_cursors.Decode(data, 24)); break;
            case 10: Cached(data); break;
            case 11: NewPointer(data); break;
            default: throw new RdpProtocolException($"Unnegotiated fast-path update code {code}.");
        }
    }
    private void Position(ReadOnlySpan<byte> bytes) { var r = new PacketReader(bytes); ushort x = r.U16(), y = r.U16(); r.End(); CursorPositionChanged?.Invoke(x, y); }
    private void Cached(ReadOnlySpan<byte> bytes) { var r = new PacketReader(bytes); ushort index = r.U16(); r.End(); CursorChanged?.Invoke(_cursors.Cached(index)); }
    private void NewPointer(ReadOnlySpan<byte> bytes) { var r = new PacketReader(bytes); int bpp = r.U16(); CursorChanged?.Invoke(_cursors.Decode(r.Bytes(r.Remaining), bpp)); }
    private void SlowPointer(ReadOnlySpan<byte> bytes)
    {
        var r = new PacketReader(bytes); int type = r.U16(); r.U16(); var body = r.Bytes(r.Remaining);
        switch (type)
        {
            case 1: var system = new PacketReader(body); uint id = system.U32(); system.End(); CursorChanged?.Invoke(new(0, 0, 0, 0, [], Hidden: id == 0, SystemDefault: id == 0x7f00)); break;
            case 3: Position(body); break;
            case 6: CursorChanged?.Invoke(_cursors.Decode(body, 24)); break;
            case 7: Cached(body); break;
            case 8: NewPointer(body); break;
            default: throw new RdpProtocolException("Unsupported pointer message.");
        }
    }
    private void StopTransport()
    {
        lock (_stopGate)
        {
            if (_released) return;
            try { _stop.Cancel(); } finally { _tcp?.Dispose(); }
        }
    }
    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        StopTransport();
        if (State is not (SessionState.Disconnected or SessionState.Disconnecting or SessionState.Faulted)) SetState(SessionState.Disconnecting);
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() => _lifetime.DisposeAsync(() => DisconnectAsync(), () =>
    {
        lock (_stopGate) { _released = true; _tcp?.Dispose(); _stop.Dispose(); }
        // SemaphoreSlim has no native handle unless AvailableWaitHandle is requested.
        // Do not dispose gates while an already-queued public write can still release one.
    });
}
