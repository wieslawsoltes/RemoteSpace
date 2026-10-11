using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RemoteSpace.Client;
using RemoteSpace.Core;
using RemoteSpace.Graphics;
using RemoteSpace.Security;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); Console.WriteLine("PASS " + name); checks++; }
byte[] nonce = Enumerable.Range(0, 32).Select(n => (byte)n).ToArray();
var message = new CredSspMessage(6, [1, 2, 3], [4, 5], [6, 7], 0xc000006d, nonce);
var decoded = CredSsp.Decode(CredSsp.Encode(message));
Check(decoded.Version == 6 && decoded.Token!.SequenceEqual(message.Token!) && decoded.AuthInfo!.SequenceEqual(message.AuthInfo!) && decoded.PublicKeyAuth!.SequenceEqual(message.PublicKeyAuth!) && decoded.Error == message.Error && decoded.Nonce!.SequenceEqual(nonce), "CredSSP ASN.1 field round trip");
Check(Convert.ToHexString(CredSsp.Encode(new(6))) == "3005A003020106", "CredSSP version golden bytes");
Check(!CredSsp.BindingHash(true, nonce, [1, 2, 3]).SequenceEqual(CredSsp.BindingHash(false, nonce, [1, 2, 3])), "channel-binding reflection resistance");
Check(!CredSsp.BindingHash(true, nonce, [1, 2, 3]).SequenceEqual(CredSsp.BindingHash(true, new byte[32], [1, 2, 3])), "channel binding includes nonce");
byte[] encoded = CredSsp.Encode(message);
for (int n = 0; n < encoded.Length; n++)
{
    bool rejected = false; try { CredSsp.Decode(encoded.AsMemory(0, n)); } catch (IOException) { rejected = true; }
    Check(rejected, "CredSSP truncation " + n);
}
Check((await CredSsp.ReadAsync(new MemoryStream(encoded), CancellationToken.None)).SequenceEqual(encoded), "CredSSP stream framing");
using var rsa = RSA.Create(2048);
var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
string pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
Check(CertificatePolicy.Validate(certificate, SslPolicyErrors.None, null, DateTimeOffset.UtcNow), "valid certificate accepted");
Check(!CertificatePolicy.Validate(certificate, SslPolicyErrors.RemoteCertificateChainErrors, null, DateTimeOffset.UtcNow), "unknown CA rejected without explicit pin");
Check(CertificatePolicy.Validate(certificate, SslPolicyErrors.RemoteCertificateChainErrors, pin, DateTimeOffset.UtcNow), "explicit exact certificate pin accepted");
Check(!CertificatePolicy.Validate(certificate, SslPolicyErrors.None, new string('0', 64), DateTimeOffset.UtcNow), "wrong certificate pin rejected");
Check(!CertificatePolicy.Validate(certificate, SslPolicyErrors.None, pin, DateTimeOffset.UtcNow.AddDays(3)), "expired pinned certificate rejected");
Console.WriteLine($"RESULT: {checks} security checks passed.");

string? host = Environment.GetEnvironmentVariable("RDP_TEST_HOST");
if (string.IsNullOrEmpty(host)) { Console.WriteLine("NETWORK: not requested; set RDP_TEST_HOST for an authorized test server."); return; }
string password = Environment.GetEnvironmentVariable("RDP_TEST_PASSWORD") ?? throw new Exception("RDP_TEST_PASSWORD is required.");
string user = Environment.GetEnvironmentVariable("RDP_TEST_USER") ?? "rdptest";
var options = new ConnectionOptions { Host = host, Security = SecurityMode.Tls, CertificateSha256 = Environment.GetEnvironmentVariable("RDP_TEST_PIN"), RedirectClipboard = true, Width = 1280, Height = 720, ConnectTimeout = TimeSpan.FromSeconds(60) };
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
await using var session = new RdpSession();
var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var displayReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var resized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var pixels = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var frame = new FrameBuffer(); long rectangles = 0; int currentWidth = 0, currentHeight = 0;
session.StateChanged += state => { Console.WriteLine("STATE " + state); if (state == SessionState.Active) active.TrySetResult(); };
session.Diagnostic += d => Console.WriteLine(d.Stage + ": " + d.Message);
session.DisplayCapabilitiesChanged += caps => { if (caps is not null) displayReady.TrySetResult(); };
session.DesktopResized += d => { frame.Resize(d.Width, d.Height); currentWidth = d.Width; currentHeight = d.Height; Console.WriteLine($"SIZE {d.Width}x{d.Height}"); };
session.SurfaceUpdated += update => { frame.Apply(update); Interlocked.Increment(ref rectangles); if (session.DecodedPixels > 100_000) pixels.TrySetResult(); if (currentWidth == 1024 && currentHeight == 768) resized.TrySetResult(); };
session.ClipboardReceived += text => Console.WriteLine("CLIPBOARD received " + text.Length + " characters");
Task running = session.RunAsync(options, new SessionCredentials(user, password), deadline.Token);
await AwaitOrFailure(active.Task); await AwaitOrFailure(pixels.Task);
// RDP activation can precede the Xorg terminal by several seconds. Wait for
// the fixture's terminal pixels on the RDP wire, not a timing guess or a
// server-side input injection. Otherwise early keystrokes go to the X root.
while (!TerminalPainted(frame.Snapshot()))
{
    Task finished = await Task.WhenAny(Task.Delay(100, deadline.Token), running);
    if (finished == running) { await running; throw new Exception("Session ended before the terminal was painted."); }
    deadline.Token.ThrowIfCancellationRequested();
}
Check(true, "remote terminal is painted before keyboard interaction");
await session.SendInputAsync(new(InputKind.Pointer, 0x0800, 0, 100, 100), deadline.Token);
await session.SendInputAsync(new(InputKind.Pointer, 0x8000 | 0x1000, 0, 100, 100), deadline.Token);
await session.SendInputAsync(new(InputKind.Pointer, 0x1000, 0, 100, 100), deadline.Token);
// The fixture has no window manager. Allow its xterm client to process focus
// events before the keyboard burst; no server-side focus or file is injected.
await Task.Delay(250, deadline.Token);
ushort[] command = [0x14, 0x18, 0x16, 0x2e, 0x23, 0x39, 0x19, 0x13, 0x18, 0x18, 0x21, 0x1c];
foreach (ushort code in command)
{
    await session.SendInputAsync(new(InputKind.ScanCode, 0, code), deadline.Token);
    await session.SendInputAsync(new(InputKind.ScanCode, 0x8000, code), deadline.Token);
    await Task.Delay(40, deadline.Token);
}
Console.WriteLine("INPUT sent pointer focus and 12 key press/release pairs to the real desktop.");
await Task.Delay(2000, deadline.Token);
Directory.CreateDirectory("artifacts/integration");
await Capture("desktop.ppm");
Check(rectangles > 1 && session.DecodedPixels >= 100_000, "live RDP desktop pixels decoded");
Console.WriteLine($"INTEROP: {rectangles} rectangles; {session.DecodedPixels} decoded pixels; {session.ReceivedBytes} received bytes.");
if (Environment.GetEnvironmentVariable("RDP_TEST_RESIZE") == "1")
{
    await AwaitOrFailure(displayReady.Task);
    await session.RequestDisplayLayoutAsync([new DisplayMonitor(0, 0, 1024, 768)], deadline.Token);
    await AwaitOrFailure(resized.Task);
    Check(currentWidth == 1024 && currentHeight == 768, "live dynamic-resolution reactivation and new framebuffer updates");
    await Capture("resized.ppm");
}
await session.DisconnectAsync(); await running;
async Task Capture(string name)
{
    var snapshot = frame.Snapshot();
    await using var file = File.Create(Path.Combine("artifacts/integration", name));
    await file.WriteAsync(Encoding.ASCII.GetBytes($"P6\n{snapshot.Width} {snapshot.Height}\n255\n"));
    byte[] rgb = new byte[snapshot.Width * snapshot.Height * 3];
    for (int src = 0, dst = 0; src < snapshot.Rgba.Length; src += 4) { rgb[dst++] = snapshot.Rgba[src]; rgb[dst++] = snapshot.Rgba[src + 1]; rgb[dst++] = snapshot.Rgba[src + 2]; }
    await file.WriteAsync(rgb);
}
async Task AwaitOrFailure(Task expected)
{
    Task finished = await Task.WhenAny(expected, running).WaitAsync(deadline.Token);
    if (finished == running) { await running; throw new Exception("Session ended before the expected integration event."); }
    await expected;
}

static bool TerminalPainted(SurfaceUpdate snapshot)
{
    int terminalPixels = 0;
    for (int i = 0; i < snapshot.Rgba.Length; i += 4)
        if (snapshot.Rgba[i] == 23 && snapshot.Rgba[i + 1] == 57 && snapshot.Rgba[i + 2] == 86 && ++terminalPixels >= 30_000)
            return true;
    return false;
}
