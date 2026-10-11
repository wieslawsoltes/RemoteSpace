using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using RemoteSpace.Client;
using RemoteSpace.Core;
using RemoteSpace.Profiles;
using RemoteSpace.Transport;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 65536);
var app = builder.Build();
string token = Environment.GetEnvironmentVariable("REMOTESPACE_TOKEN") ?? throw new InvalidOperationException("REMOTESPACE_TOKEN is required. Generate a random token with at least 32 characters.");
if (token.Length is < 32 or > 512) throw new InvalidOperationException("REMOTESPACE_TOKEN must contain 32–512 characters.");
byte[] tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token)); token = "";
var origins = (Environment.GetEnvironmentVariable("REMOTESPACE_ORIGINS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
var targets = (Environment.GetEnvironmentVariable("REMOTESPACE_TARGETS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(RdpFile.ParseAddress).ToArray();
if (origins.Count == 0 || targets.Length == 0) throw new InvalidOperationException("Explicit REMOTESPACE_ORIGINS and REMOTESPACE_TARGETS allowlists are required (semicolon-separated).");
using var sessions = new SemaphoreSlim(8);
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.Use(async (context, next) => { context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.XContentTypeOptions = "nosniff"; await next(context); });
app.MapGet("/health", () => Results.Json(new { service = "RemoteSpace bridge", protocol = BridgeWire.SubProtocol, status = "ready" }));
app.Map("/session", async context =>
{
    if (!context.Request.IsHttps && !IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None)) { context.Response.StatusCode = 400; return; }
    if (!context.WebSockets.IsWebSocketRequest || !origins.Contains(context.Request.Headers.Origin.ToString()) || !context.WebSockets.WebSocketRequestedProtocols.Contains(BridgeWire.SubProtocol)) { context.Response.StatusCode = 403; return; }
    if (!await sessions.WaitAsync(0, context.RequestAborted)) { context.Response.StatusCode = 503; return; }
    try
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync(BridgeWire.SubProtocol);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        using var authentication = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); authentication.CancelAfter(TimeSpan.FromSeconds(5));
        BridgePacket? first = await BridgeWire.ReceiveAsync(socket, authentication.Token, 16384);
        if (first is null || first.Binary) return;
        BridgeControl request;
        try { request = BridgeWire.ParseControl(first.Payload); }
        finally { CryptographicOperations.ZeroMemory(first.Payload); }
        bool authorized = request.Type == "connect" && request.Token is { Length: >= 32 and <= 512 } supplied &&
            CryptographicOperations.FixedTimeEquals(tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
        request.Token = null;
        if (!authorized) { await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Authentication failed", authentication.Token); return; }
        if (request.Options is null || !targets.Any(target => string.Equals(target.Host, request.Options.Host, StringComparison.OrdinalIgnoreCase) && target.Port == request.Options.Port))
        { await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Target is not allowed", authentication.Token); return; }
        request.Options.Validate();
        if ((request.UserName?.Length ?? 0) > 255 || (request.Password?.Length ?? 0) > 4096 || (request.Domain?.Length ?? 0) > 255) throw new InvalidDataException("Credential fields exceed the configured limits.");
        var credentials = new SessionCredentials(request.UserName ?? "", request.Password ?? "", request.Domain ?? ""); request.Password = null;
        await using var session = new RdpSession();
        var outgoing = Channel.CreateBounded<BridgePacket>(new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        long queuedBytes = 0;
        void Queue(BridgePacket packet)
        {
            if (lifetime.IsCancellationRequested) return;
            if (Interlocked.Add(ref queuedBytes, packet.Payload.Length) > 32 * 1024 * 1024 || !outgoing.Writer.TryWrite(packet))
            {
                Interlocked.Add(ref queuedBytes, -packet.Payload.Length);
                lifetime.Cancel();
            }
        }
        void Control(BridgeControl message) => Queue(new(false, BridgeWire.Control(message)));
        session.DisplayCapabilitiesChanged += caps => { if (request.EnableDisplayControl) Control(new() { Type = "display", DisplayCapabilities = caps }); };
        session.StateChanged += state => Control(new() { Type = "state", State = state });
        session.DesktopResized += size => Control(new() { Type = "size", Width = size.Width, Height = size.Height });
        session.SurfaceUpdated += update => { foreach (var part in BridgeWire.Split(update)) Queue(new(true, BridgeWire.Surface(part))); };
        session.Diagnostic += d => Control(new() { Type = "diagnostic", Stage = d.Stage, Message = d.Message });
        session.ClipboardReceived += text => Control(new() { Type = "clipboard", Text = text });
        session.CursorChanged += cursor => Control(new() { Type = "cursor", Cursor = cursor });
        session.CursorPositionChanged += (x, y) => Control(new() { Type = "position", Width = x, Height = y });
        async Task SendLoop()
        {
            await foreach (var packet in outgoing.Reader.ReadAllAsync(lifetime.Token))
            {
                try { await socket.SendAsync(packet.Payload.AsMemory(), packet.Binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, lifetime.Token); }
                finally { Interlocked.Add(ref queuedBytes, -packet.Payload.Length); }
            }
        }
        async Task ReceiveLoop()
        {
            long window = Environment.TickCount64; int inputs = 0, clipboards = 0, layouts = 0;
            while (!lifetime.IsCancellationRequested)
            {
                var packet = await BridgeWire.ReceiveAsync(socket, lifetime.Token); if (packet is null) break;
                if (packet.Binary) throw new InvalidDataException("Client binary messages are not accepted.");
                var command = BridgeWire.ParseControl(packet.Payload);
                if (Environment.TickCount64 - window >= 1000) { window = Environment.TickCount64; inputs = 0; clipboards = 0; layouts = 0; }
                if (++inputs > 2000) throw new InvalidDataException("Input rate limit exceeded.");
                switch (command.Type)
                {
                    case "input": await session.SendInputAsync(command.Input, lifetime.Token); break;
                    case "clipboard":
                        if (++clipboards > 4) throw new InvalidDataException("Clipboard rate limit exceeded.");
                        await session.SendClipboardAsync(command.Text ?? "", lifetime.Token); break;
                    case "display-layout":
                        if (!request.EnableDisplayControl || ++layouts > 4 || command.Monitors is null) throw new InvalidDataException("Display control is unavailable or its rate limit was exceeded.");
                        await session.RequestDisplayLayoutAsync(command.Monitors, lifetime.Token); break;
                    case "disconnect": return;
                    default: throw new InvalidDataException("Unknown client control message.");
                }
            }
        }
        async Task RunSession()
        {
            try { await session.RunAsync(request.Options, credentials, lifetime.Token); }
            catch (Exception e) when (!lifetime.IsCancellationRequested) { Control(new() { Type = "error", Message = e.Message }); }
        }
        Task send = SendLoop(), receive = ReceiveLoop(), run = RunSession();
        try
        {
            Task ended = await Task.WhenAny(send, receive, run);
            if (ended == run) { outgoing.Writer.TryComplete(); await send.WaitAsync(TimeSpan.FromSeconds(3)); }
            await ended;
        }
        finally
        {
            lifetime.Cancel(); await session.DisconnectAsync(); outgoing.Writer.TryComplete();
            try { await Task.WhenAll(send, receive, run); } catch (Exception) when (lifetime.IsCancellationRequested) { }
            socket.Abort();
        }
    }
    catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ArgumentException or TimeoutException or System.Text.Json.JsonException)
    { app.Logger.LogInformation("RemoteSpace session ended: {ErrorType}", e.GetType().Name); }
    finally { sessions.Release(); }
});
app.Run();
