namespace RemoteSpace.Core;

public enum SessionState { Disconnected, Connecting, Negotiating, Securing, Authenticating, Establishing, Active, Disconnecting, Faulted }
public enum SecurityMode { NetworkLevelAuthentication, Tls }
public enum InputKind { ScanCode, Unicode, Pointer, Synchronize, ExtendedPointer }
public readonly record struct RemoteInput(InputKind Kind, ushort Flags, ushort Code, ushort X = 0, ushort Y = 0);
public sealed record SurfaceUpdate(int X, int Y, int Width, int Height, byte[] Rgba);
public sealed record DesktopSize(int Width, int Height);
public sealed record SessionDiagnostic(DateTimeOffset Time, string Stage, string Message);

public sealed record ConnectionOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 3389;
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public int BitsPerPixel { get; init; } = 24;
    public SecurityMode Security { get; init; } = SecurityMode.NetworkLevelAuthentication;
    public string? CertificateSha256 { get; init; }
    public bool EnableDynamicResolution { get; init; } = true;
    public bool RedirectClipboard { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public uint KeyboardLayout { get; init; } = 0x0409;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 || Host.Any(char.IsControl) || Host.Contains('/') || Host.Contains('\\'))
            throw new ArgumentException("A DNS name or IP address is required.", nameof(Host));
        if (Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (Width is < 200 or > 8192 || Height is < 200 or > 8192 || (long)Width * Height > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(Width), "Desktop dimensions must be 200..8192 and at most 16 megapixels.");
        if (BitsPerPixel is not (16 or 24)) throw new ArgumentOutOfRangeException(nameof(BitsPerPixel));
        if (!Enum.IsDefined(Security)) throw new ArgumentOutOfRangeException(nameof(Security));
        if (ConnectTimeout <= TimeSpan.Zero || ConnectTimeout > TimeSpan.FromMinutes(2)) throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        if (CertificateSha256 is { Length: > 0 } pin && (pin.Length != 64 || !pin.All(Uri.IsHexDigit)))
            throw new ArgumentException("Certificate pin must contain exactly 64 hexadecimal SHA-256 characters.", nameof(CertificateSha256));
    }
}

// Credentials are deliberately separate from serializable connection profiles.
public sealed class SessionCredentials(string userName, string password, string domain = "")
{
    public string UserName { get; } = userName;
    public string Password { get; } = password;
    public string Domain { get; } = domain;
    public override string ToString() => "[RemoteSpace credentials: redacted]";
}

public interface IRemoteSession : IAsyncDisposable
{
    SessionState State { get; }
    event Action<SessionState>? StateChanged;
    event Action<DesktopSize>? DesktopResized;
    event Action<SurfaceUpdate>? SurfaceUpdated;
    event Action<SessionDiagnostic>? Diagnostic;
    event Action<string>? ClipboardReceived;
    Task RunAsync(ConnectionOptions options, SessionCredentials credentials, CancellationToken cancellationToken = default);
    ValueTask SendInputAsync(RemoteInput input, CancellationToken cancellationToken = default);
    ValueTask SendClipboardAsync(string text, CancellationToken cancellationToken = default);
    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);
}
