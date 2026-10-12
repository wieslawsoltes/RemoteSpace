using System.Globalization;
using System.Text;
using RemoteSpace.Core;

namespace RemoteSpace.Profiles;

public sealed record ConnectionProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "New PC";
    public string Group { get; init; } = "Saved PCs";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 3389;
    public string UserName { get; init; } = "";
    public string Domain { get; init; } = "";
    public bool Favorite { get; init; }
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public int BitsPerPixel { get; init; } = 24;
    public SecurityMode Security { get; init; } = SecurityMode.NetworkLevelAuthentication;
    public string? CertificateSha256 { get; init; }
    public bool RedirectClipboard { get; init; }
    public bool FitSessionToWindow { get; init; } = true;
    public double LocalZoom { get; init; } = 1;
    public bool UpdateResolutionOnResize { get; init; }

    /// <summary>Initial local presentation only; never changes negotiated pixels or server DPI.</summary>
    public double? GetLocalDisplayScale()
    {
        if (!double.IsFinite(LocalZoom) || LocalZoom is < .1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(LocalZoom), "Local zoom must be between 10% and 400%.");
        return UpdateResolutionOnResize || FitSessionToWindow ? null : LocalZoom;
    }
    public ConnectionOptions ToOptions()
    {
        _ = GetLocalDisplayScale();
        return new() { Host = Host, Port = Port, Width = Width, Height = Height,
            BitsPerPixel = BitsPerPixel, Security = Security, CertificateSha256 = CertificateSha256, RedirectClipboard = RedirectClipboard };
    }
}

public sealed record RdpImportResult(ConnectionProfile Profile, IReadOnlyList<string> Warnings);

/// <summary>Strict typed .rdp import/export. Passwords, signatures, and unsupported options are never silently enabled.</summary>
public static class RdpFile
{
    public const int MaxBytes = 1_048_576;
    public static RdpImportResult Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes) throw new ArgumentException("RDP file exceeds the one-megabyte limit.");
        string text = bytes.StartsWith(new byte[] { 255, 254 }) ? new UnicodeEncoding(false, true, true).GetString(bytes[2..])
            : bytes.StartsWith(new byte[] { 254, 255 }) ? new UnicodeEncoding(true, true, true).GetString(bytes[2..])
            : new UTF8Encoding(false, true).GetString(bytes.StartsWith(new byte[] { 239, 187, 191 }) ? bytes[3..] : bytes);
        return Parse(text);
    }
    public static RdpImportResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxBytes) throw new ArgumentException("RDP file is too large.");
        var values = new Dictionary<string, (char Type, string Value)>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            int colon = line.IndexOf(':');
            if (colon < 1 || colon + 2 >= line.Length || line[colon + 2] != ':') throw new FormatException("Malformed RDP setting.");
            string key = line[..colon].Trim(); char type = line[colon + 1]; string value = line[(colon + 3)..];
            if (type is not ('s' or 'i' or 'b')) throw new FormatException($"Unsupported field type for {key}.");
            if (!values.TryAdd(key, (type, value))) throw new FormatException($"Duplicate RDP setting: {key}.");
        }
        string String(string key, string fallback = "")
        {
            if (!values.Remove(key, out var entry)) return fallback;
            if (entry.Type != 's') throw new FormatException($"{key} must be a string.");
            return entry.Value;
        }
        int Integer(string key, int fallback)
        {
            if (!values.Remove(key, out var entry)) return fallback;
            if (entry.Type != 'i' || entry.Value.Contains('\0') || !int.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                throw new FormatException($"{key} must be an integer.");
            return number;
        }
        string address = String("full address");
        (string host, int port) = ParseAddress(address);
        int nla = Integer("enablecredsspsupport", 1);
        if (nla is not (0 or 1)) throw new FormatException("enablecredsspsupport must be 0 or 1.");
        int clipboard = Integer("redirectclipboard", 0);
        if (clipboard is not (0 or 1)) throw new FormatException("redirectclipboard must be 0 or 1.");
        int smartSizing = Integer("smart sizing", 0);
        if (smartSizing is not (0 or 1)) throw new FormatException("smart sizing must be 0 or 1.");
        int dynamicResolution = Integer("dynamic resolution", 1);
        if (dynamicResolution is not (0 or 1)) throw new FormatException("dynamic resolution must be 0 or 1.");
        var profile = new ConnectionProfile { Host = host, Port = port, Name = String("remoteapplicationname", host),
            UserName = String("username"), Domain = String("domain"), Width = Integer("desktopwidth", 1280),
            Height = Integer("desktopheight", 720), BitsPerPixel = Integer("session bpp", 24),
            Security = nla == 0 ? SecurityMode.Tls : SecurityMode.NetworkLevelAuthentication, RedirectClipboard = clipboard == 1, FitSessionToWindow = smartSizing == 1, UpdateResolutionOnResize = dynamicResolution == 1 };
        foreach (var key in values.Keys.Order(StringComparer.OrdinalIgnoreCase))
            warnings.Add(key.StartsWith("password", StringComparison.OrdinalIgnoreCase) ? "Embedded credentials were discarded." : $"Setting '{key}' is not implemented and was not applied.");
        profile.ToOptions().Validate();
        return new(profile, warnings);
    }
    public static (string Host, int Port) ParseAddress(string address) => ParseAddressWithDefaultPort(address, 3389);
    public static (string Host, int Port) ParseAddressWithDefaultPort(string address, int defaultPort)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (defaultPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(defaultPort));
        string host = address.Trim(); int port = defaultPort;
        if (host.StartsWith('['))
        {
            int close = host.IndexOf(']');
            if (close < 2) throw new FormatException("Invalid bracketed IPv6 address.");
            string suffix = host[(close + 1)..]; string ip = host[1..close];
            if (suffix.Length > 0)
            {
                if (!suffix.StartsWith(':') || !int.TryParse(suffix.AsSpan(1), out port)) throw new FormatException("Invalid RDP port.");
            }
            host = ip;
        }
        else if (host.Count(c => c == ':') == 1)
        {
            int index = host.LastIndexOf(':');
            if (!int.TryParse(host.AsSpan(index + 1), out port)) throw new FormatException("Invalid RDP port.");
            host = host[..index];
        }
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535) throw new FormatException("Invalid RDP endpoint.");
        return (host, port);
    }
    public static string Export(ConnectionProfile profile)
    {
        profile.ToOptions().Validate();
        static string Safe(string value) => value.IndexOfAny(['\r', '\n', '\0']) < 0 ? value : throw new ArgumentException("RDP strings cannot contain line breaks or NUL.");
        string host = profile.Host.Contains(':') ? $"[{profile.Host}]" : profile.Host;
        return $"full address:s:{Safe(host)}:{profile.Port}\r\nusername:s:{Safe(profile.UserName)}\r\ndomain:s:{Safe(profile.Domain)}\r\ndesktopwidth:i:{profile.Width}\r\ndesktopheight:i:{profile.Height}\r\nsession bpp:i:{profile.BitsPerPixel}\r\nenablecredsspsupport:i:{(profile.Security == SecurityMode.NetworkLevelAuthentication ? 1 : 0)}\r\nredirectclipboard:i:{(profile.RedirectClipboard ? 1 : 0)}\r\nsmart sizing:i:{(profile.FitSessionToWindow ? 1 : 0)}\r\ndynamic resolution:i:{(profile.UpdateResolutionOnResize ? 1 : 0)}\r\n";
    }
}
