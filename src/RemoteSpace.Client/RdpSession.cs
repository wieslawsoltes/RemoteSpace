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
public sealed class RdpSession : IRemoteSession, IRemoteCursorSource
{
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ChannelAssembler _clipboardFragments = new();
    private readonly FastPathAssembler _fastPath = new();
    private readonly CursorDecoder _cursors = new();
    private TcpClient? _tcp;
    private Stream? _stream;
    private ConnectionOptions? _options;
    private DemandActive? _desktop;
    private ushort _user, _global, _clipboardChannel;
    private int _started;
    private uint[]? _palette;
    private bool _longNames, _clipboardReady, _clipboardPending;
    private string? _localClipboard;
    public SessionState State { get; private set; }
    public event Action<SessionState>? StateChanged;
    public event Action<DesktopSize>? DesktopResized;
    public event Action<SurfaceUpdate>? SurfaceUpdated;
    public event Action<SessionDiagnostic>? Diagnostic;
    public event Action<string>? ClipboardReceived;
    public event Action<RemoteCursor>? CursorChanged;
    public event Action<ushort, ushort>? CursorPositionChanged;
    public long ReceivedBytes { get; private set; }
    public long DecodedPixels { get; private set; }
    private void SetState(SessionState state) { State = state; StateChanged?.Invoke(state); }
    private void Log(string stage, string message) => Diagnostic?.Invoke(new(DateTimeOffset.UtcNow, stage, message));

    public async Task RunAsync(ConnectionOptions options, SessionCredentials credentials, CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Create a new session object to reconnect.");
        _options = options;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        handshake.CancelAfter(options.ConnectTimeout);
        try
        {
            SetState(SessionState.Connecting); _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(options.Host, options.Port, handshake.Token); _stream = _tcp.GetStream();
            SetState(SessionState.Negotiating);
            var requested = options.Security == SecurityMode.NetworkLevelAuthentication ? SecurityProtocols.Hybrid : SecurityProtocols.Tls;
            await WriteAsync(Negotiation.Request(requested), handshake.Token);
            var confirm = await Framing.ReadAsync(_stream, handshake.Token);
            if (confirm.IsFastPath) throw new RdpProtocolException("Fast-path packet during security negotiation.");
            var selected = Negotiation.ParseConfirm(confirm.Payload, requested);
            SetState(SessionState.Securing);
            var tls = new SslStream(_stream, false, (_, certificate, _, errors) =>
            {
                bool valid = CertificatePolicy.Validate(certificate, errors, options.CertificateSha256, DateTimeOffset.UtcNow);
                if (!valid && certificate is not null) Log("Certificate", "Certificate validation failed. Verify its SHA-256 fingerprint out of band: " + certificate.GetCertHashString(HashAlgorithmName.SHA256));
                return valid;
            });
            _stream = tls;
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = options.Host, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.Online, AllowTlsResume = false
            }, handshake.Token);
            Log("TLS", $"Authenticated {tls.SslProtocol}; cipher {tls.NegotiatedCipherSuite}.");
            if (selected.Protocol == SecurityProtocols.Hybrid)
            {
                SetState(SessionState.Authenticating);
                await CredSsp.AuthenticateAsync(tls, options.Host, credentials, handshake.Token);
                Log("CredSSP", "Modern channel binding verified; credentials delegated over the protected channel.");
            }
            SetState(SessionState.Establishing);
            await WriteMcsAsync(Mcs.ConnectInitial(options, selected.Protocol), handshake.Token);
            var settings = Mcs.ParseConnectResponse(await ReadMcsAsync(handshake.Token), options.RedirectClipboard ? 1 : 0);
            if (settings.RequestedProtocols is { } protocols && protocols != (uint)requested) throw new RdpProtocolException("Server's security negotiation echo did not match.");
            _global = settings.GlobalChannel; _clipboardChannel = settings.StaticChannels.FirstOrDefault();
            await WriteMcsAsync(Mcs.ErectDomain(), handshake.Token);
            await WriteMcsAsync(Mcs.AttachUser(), handshake.Token);
            _user = Mcs.ParseAttachConfirm(await ReadMcsAsync(handshake.Token));
            foreach (ushort channel in new[] { _user, _global }.Concat(settings.StaticChannels).Distinct())
            {
                await WriteMcsAsync(Mcs.Join(_user, channel), handshake.Token);
                Mcs.ParseJoinConfirm(await ReadMcsAsync(handshake.Token), _user, channel);
            }
            byte[] info = Activation.ClientInfo(options, credentials, selected.Protocol == SecurityProtocols.Hybrid);
            try { await SendChannelAsync(_global, info, handshake.Token); }
            finally { CryptographicOperations.ZeroMemory(info); }
            while (!lifetime.IsCancellationRequested)
            {
                CancellationToken ct = State == SessionState.Active ? lifetime.Token : handshake.Token;
                var packet = await Framing.ReadAsync(_stream, ct); ReceivedBytes += packet.Payload.Length;
                if (packet.IsFastPath) _fastPath.Accept(packet.Payload, FastUpdate);
                else
                {
                    byte[] mcs = Framing.UnwrapData(packet.Payload);
                    if (mcs.Length > 0 && (mcs[0] & 0xfc) == 0x20) { Log("Session", "Server disconnected the transport."); break; }
                    var channel = Mcs.ParseData(mcs);
                    if (channel.Channel == _global) await ProcessGlobalAsync(channel.Payload, ct);
                    else if (channel.Channel == _clipboardChannel && _clipboardChannel != 0)
                    {
                        byte[]? clipboard = _clipboardFragments.Accept(channel.Payload);
                        if (clipboard is not null) await ProcessClipboardAsync(ClipboardCodec.Parse(clipboard), ct);
                    }
                    else throw new RdpProtocolException("Data arrived on an unjoined channel.");
                }
                if (State == SessionState.Active) handshake.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) when (lifetime.IsCancellationRequested) { }
        catch (Exception e)
        {
            SetState(SessionState.Faulted); Log("Error", e is OperationCanceledException ? "Connection establishment timed out." : e.Message); throw;
        }
        finally
        {
            if (_stream is not null) await _stream.DisposeAsync(); _tcp?.Dispose();
            _localClipboard = null; if (State != SessionState.Faulted) SetState(SessionState.Disconnected); _completed.TrySetResult();
        }
    }
    private async ValueTask<byte[]> ReadMcsAsync(CancellationToken ct)
    {
        var packet = await Framing.ReadAsync(_stream!, ct);
        if (packet.IsFastPath) throw new RdpProtocolException("Unexpected fast-path packet during MCS establishment.");
        return Framing.UnwrapData(packet.Payload);
    }
    private async ValueTask WriteAsync(byte[] data, CancellationToken ct)
    {
        await _writer.WaitAsync(ct);
        try { await (_stream ?? throw new InvalidOperationException("The transport is not connected.")).WriteAsync(data, ct); }
        finally { _writer.Release(); }
    }
    private ValueTask WriteMcsAsync(byte[] data, CancellationToken ct) => WriteAsync(Framing.Data(data), ct);
    private ValueTask SendChannelAsync(ushort channel, byte[] data, CancellationToken ct) => WriteMcsAsync(Mcs.SendData(_user, channel, data), ct);
    private ValueTask SendShareAsync(byte[] data, CancellationToken ct) => SendChannelAsync(_global, data, ct);

    private async ValueTask ProcessGlobalAsync(byte[] payload, CancellationToken ct)
    {
        if (payload.Length >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2)) == 0 && (payload[0] & 0x80) != 0)
        {
            var r = new PacketReader(payload); ushort flags = r.U16(); r.U16();
            if ((flags & ~0x0280) != 0) throw new RdpProtocolException("Unexpected licensing security flags.");
            byte type = r.U8(); r.U8(); int length = r.U16();
            if (length != payload.Length - 4) throw new RdpProtocolException("Invalid license message length.");
            if (type != 0xff) throw new NotSupportedException("This server requires the full RDS license exchange, which is not yet implemented. No license bypass is attempted.");
            uint status = r.U32(), transition = r.U32(); r.U16(); int blobLength = r.U16(); r.Skip(blobLength); r.End();
            if (status != 7 || transition != 2) throw new RdpProtocolException($"Server licensing failed: 0x{status:X8}.");
            Log("Licensing", "Server accepted this client."); return;
        }
        foreach (var share in Activation.ParseShares(payload))
        {
            switch (share.Type)
            {
                case 1:
                    _desktop = Activation.ParseDemand(share.Body); SetState(SessionState.Establishing);
                    DesktopResized?.Invoke(new(_desktop.Width, _desktop.Height));
                    await SendShareAsync(Activation.Confirm(_desktop, _user, _options!.KeyboardLayout), ct);
                    await SendShareAsync(Activation.Synchronize(_desktop.ShareId, _user, share.Source), ct);
                    await SendShareAsync(Activation.Control(_desktop.ShareId, _user, 4), ct);
                    await SendShareAsync(Activation.Control(_desktop.ShareId, _user, 1), ct);
                    await SendShareAsync(Activation.FontList(_desktop.ShareId, _user), ct);
                    break;
                case 6: SetState(SessionState.Establishing); Log("Session", "Server deactivated the desktop; awaiting reactivation."); break;
                case 7:
                    var data = Activation.ParseData(share.Body);
                    if (_desktop is null || data.ShareId != _desktop.ShareId) throw new RdpProtocolException("Share data referenced an inactive desktop.");
                    switch (data.Type)
                    {
                        case 2: SlowUpdate(data.Body); break;
                        case 27: SlowPointer(data.Body); break;
                        case 40:
                            SetState(SessionState.Active); Log("Session", $"Desktop active: {_desktop.Width}×{_desktop.Height}, {_desktop.BitsPerPixel}-bit bitmap updates.");
                            await SendShareAsync(Activation.Input(_desktop.ShareId, _user, new(InputKind.Synchronize, 0, 0)), ct);
                            await SendShareAsync(Activation.Refresh(_desktop.ShareId, _user, _desktop.Width, _desktop.Height), ct); break;
                        case 47:
                            var e = new PacketReader(data.Body); uint error = e.U32(); e.End();
                            if (error != 0) throw new RdpProtocolException($"Server RDP error information: 0x{error:X8}."); break;
                        case 20: case 31: case 38: case 37: break;
                        default: Log("Protocol", $"Received optional share data type {data.Type}; no handler is installed."); break;
                    }
                    break;
                default: throw new RdpProtocolException($"Unsupported share control PDU {share.Type}.");
            }
        }
    }
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
    private async ValueTask SendClipboardPacketAsync(byte[] data, CancellationToken ct)
    {
        foreach (byte[] chunk in ChannelAssembler.Fragment(data)) await SendChannelAsync(_clipboardChannel, chunk, ct);
    }
    private async ValueTask ProcessClipboardAsync(ClipboardPacket packet, CancellationToken ct)
    {
        switch (packet.Type)
        {
            case 7: _longNames = ClipboardCodec.SupportsLongNames(packet.Data); break;
            case 1:
                _clipboardReady = true;
                await SendClipboardPacketAsync(ClipboardCodec.Capabilities(), ct);
                await SendClipboardPacketAsync(ClipboardCodec.Pdu(2, 0, []), ct); break;
            case 2:
                bool text = ClipboardCodec.HasUnicodeText(packet.Data, _longNames);
                await SendClipboardPacketAsync(ClipboardCodec.Pdu(3, 1, []), ct);
                if (text && !_clipboardPending) { _clipboardPending = true; await SendClipboardPacketAsync(ClipboardCodec.Pdu(4, 0, new PacketWriter().U32(13).ToArray()), ct); }
                break;
            case 4:
                var request = new PacketReader(packet.Data); uint format = request.U32(); request.End();
                await SendClipboardPacketAsync(format == 13 && _localClipboard is not null ? ClipboardCodec.TextResponse(_localClipboard) : ClipboardCodec.Pdu(5, 2, []), ct); break;
            case 5:
                if (!_clipboardPending) throw new RdpProtocolException("Unsolicited clipboard response.");
                _clipboardPending = false;
                if ((packet.Flags & 1) != 0) ClipboardReceived?.Invoke(ClipboardCodec.ReadText(packet.Data)); break;
            case 3: break;
            default: Log("Clipboard", $"Unsupported clipboard message {packet.Type}; file and device redirection is not negotiated."); break;
        }
    }
    public ValueTask SendInputAsync(RemoteInput input, CancellationToken cancellationToken = default)
    {
        if (State != SessionState.Active || _desktop is null) throw new InvalidOperationException("The remote desktop is not active.");
        if (input.Kind == InputKind.Pointer && (input.X >= _desktop.Width || input.Y >= _desktop.Height)) throw new ArgumentOutOfRangeException(nameof(input));
        return SendShareAsync(Activation.Input(_desktop.ShareId, _user, input), cancellationToken);
    }
    public async ValueTask SendClipboardAsync(string text, CancellationToken cancellationToken = default)
    {
        if (State != SessionState.Active || !_clipboardReady || _clipboardChannel == 0) throw new InvalidOperationException("Clipboard redirection was not enabled or is not ready.");
        ClipboardCodec.TextResponse(text); _localClipboard = text;
        await SendClipboardPacketAsync(ClipboardCodec.FormatList(_longNames), cancellationToken);
    }
    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (State is not (SessionState.Disconnected or SessionState.Faulted)) SetState(SessionState.Disconnecting);
        _stop.Cancel(); _tcp?.Dispose(); return ValueTask.CompletedTask;
    }
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(); if (Volatile.Read(ref _started) != 0) await _completed.Task;
        _stop.Dispose(); _writer.Dispose();
    }
}
