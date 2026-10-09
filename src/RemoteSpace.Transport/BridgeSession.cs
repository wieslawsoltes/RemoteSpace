using System.Net.WebSockets;
using System.Security.Cryptography;
using RemoteSpace.Core;

namespace RemoteSpace.Transport;

/// <summary>Browser-capable session adapter. Authentication tokens and passwords are sent once, never in the URL.</summary>
public sealed class BridgeSession : IRemoteSession, IRemoteCursorSource
{
    private readonly Uri _endpoint;
    private string? _token;
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;
    public SessionState State { get; private set; }
    public event Action<SessionState>? StateChanged;
    public event Action<DesktopSize>? DesktopResized;
    public event Action<SurfaceUpdate>? SurfaceUpdated;
    public event Action<SessionDiagnostic>? Diagnostic;
    public event Action<string>? ClipboardReceived;
    public event Action<RemoteCursor>? CursorChanged;
    public event Action<ushort, ushort>? CursorPositionChanged;
    public BridgeSession(Uri endpoint, string token, string? nativeOrigin = null)
    {
        if (!endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
            (endpoint.Scheme != "wss" && !(endpoint.Scheme == "ws" && endpoint.IsLoopback)))
            throw new ArgumentException("Use a wss:// bridge endpoint without credentials or query parameters. ws:// is allowed only for loopback development.", nameof(endpoint));
        if (token.Length is < 32 or > 512) throw new ArgumentException("The bridge token must contain 32–512 characters.", nameof(token));
        _endpoint = endpoint; _token = token; _socket.Options.AddSubProtocol(BridgeWire.SubProtocol);
        if (!OperatingSystem.IsBrowser() && nativeOrigin is not null) _socket.Options.SetRequestHeader("Origin", nativeOrigin);
    }
    private void SetState(SessionState state) { State = state; StateChanged?.Invoke(state); }
    public async Task RunAsync(ConnectionOptions options, SessionCredentials credentials, CancellationToken cancellationToken = default)
    {
        options.Validate(); if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Create a new bridge session to reconnect.");
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        try
        {
            SetState(SessionState.Connecting);
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct.Token); connect.CancelAfter(options.ConnectTimeout);
            await _socket.ConnectAsync(_endpoint, connect.Token);
            byte[] request = BridgeWire.Control(new() { Type = "connect", Token = _token, Options = options, UserName = credentials.UserName, Password = credentials.Password, Domain = credentials.Domain });
            try { await SendAsync(request, connect.Token); }
            finally { CryptographicOperations.ZeroMemory(request); _token = null; }
            while (!ct.IsCancellationRequested)
            {
                var packet = await BridgeWire.ReceiveAsync(_socket, ct.Token); if (packet is null) break;
                if (packet.Binary) { SurfaceUpdated?.Invoke(BridgeWire.ParseSurface(packet.Payload)); continue; }
                var message = BridgeWire.ParseControl(packet.Payload);
                switch (message.Type)
                {
                    case "state": if (!Enum.IsDefined(message.State)) throw new InvalidDataException("Unknown session state."); SetState(message.State); break;
                    case "size":
                        if (message.Width is < 1 or > 8192 || message.Height is < 1 or > 8192 || (long)message.Width * message.Height > 16_777_216) throw new InvalidDataException("Invalid desktop dimensions.");
                        DesktopResized?.Invoke(new(message.Width, message.Height)); break;
                    case "diagnostic": Diagnostic?.Invoke(new(DateTimeOffset.UtcNow, message.Stage ?? "Bridge", message.Message ?? "")); break;
                    case "clipboard": ClipboardReceived?.Invoke(message.Text ?? ""); break;
                    case "cursor": if (message.Cursor is not null) CursorChanged?.Invoke(message.Cursor); break;
                    case "position": CursorPositionChanged?.Invoke(checked((ushort)message.Width), checked((ushort)message.Height)); break;
                    case "error": throw new IOException(message.Message ?? "The bridge rejected the session.");
                    default: throw new InvalidDataException("Unknown bridge control message.");
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception e) { SetState(SessionState.Faulted); Diagnostic?.Invoke(new(DateTimeOffset.UtcNow, "Error", e.Message)); throw; }
        finally { _socket.Abort(); _token = null; if (State != SessionState.Faulted) SetState(SessionState.Disconnected); _completed.TrySetResult(); }
    }
    private async ValueTask SendAsync(byte[] data, CancellationToken ct)
    {
        await _writer.WaitAsync(ct);
        try { await _socket.SendAsync(data.AsMemory(), WebSocketMessageType.Text, true, ct); }
        finally { _writer.Release(); }
    }
    public ValueTask SendInputAsync(RemoteInput input, CancellationToken cancellationToken = default)
    {
        if (State != SessionState.Active) throw new InvalidOperationException("The remote desktop is not active.");
        return SendAsync(BridgeWire.Control(new() { Type = "input", Input = input }), cancellationToken);
    }
    public ValueTask SendClipboardAsync(string text, CancellationToken cancellationToken = default)
    {
        if (State != SessionState.Active || text.Length > 524287 || text.Contains('\0')) throw new InvalidOperationException("Clipboard is unavailable or the text exceeds the limit.");
        return SendAsync(BridgeWire.Control(new() { Type = "clipboard", Text = text }), cancellationToken);
    }
    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (State is not (SessionState.Disconnected or SessionState.Faulted)) SetState(SessionState.Disconnecting);
        _stop.Cancel(); _socket.Abort(); return ValueTask.CompletedTask;
    }
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(); if (_started != 0) await _completed.Task;
        _socket.Dispose(); _writer.Dispose(); _stop.Dispose();
    }
}
