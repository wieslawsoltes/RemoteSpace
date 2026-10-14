using System.Text.Json.Serialization;
using RemoteSpace.Core;
using RemoteSpace.Profiles;

namespace RemoteSpace.Workspace;

// .NET 10 source-generated init-only deserialization assigns omitted properties
// their CLR defaults. Use private-to-the-assembly mutable read contracts so field
// initializers run and only supplied JSON properties replace them. The public
// catalog remains immutable; none of these input objects escape the parser.
internal sealed class WorkspaceReadDocument
{
    public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; }
    public WorkspaceReadSettings? Settings { get; set; } = new();
    public WorkspaceReadDevice?[]? Devices { get; set; } = [];
    public WorkspaceDocument ToDocument()
    {
        if (Settings is null || Devices is null || Devices.Length > DeviceCatalog.MaximumDevices)
            throw new FormatException("Missing settings, missing devices or an oversized device list.");
        return new()
        {
            SchemaVersion = SchemaVersion, Revision = Revision, Settings = Settings.ToSettings(),
            Devices = Devices.Select(d => d?.ToDevice() ?? throw new FormatException("Null saved device.")).ToArray()
        };
    }
}
internal sealed class WorkspaceReadSettings
{
    public WorkspaceTheme Theme { get; set; }
    public ProfileSort Sort { get; set; }
    public bool ListView { get; set; }
    public bool SortDescending { get; set; }
    public string? BridgeEndpoint { get; set; } = "";
    public WorkspaceSettings ToSettings() => new()
    {
        Theme = Theme, Sort = Sort, ListView = ListView, SortDescending = SortDescending,
        BridgeEndpoint = BridgeEndpoint ?? throw new FormatException("Null bridge endpoint preference.")
    };
}
internal sealed class WorkspaceReadDevice
{
    public WorkspaceReadProfile? Profile { get; set; }
    public DateTimeOffset? LastConnected { get; set; }
    public SavedDevice ToDevice() => new(
        Profile?.ToProfile() ?? throw new FormatException("Missing saved-device profile."), LastConnected);
}
internal sealed class WorkspaceReadProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Name { get; set; } = "New PC";
    public string? Group { get; set; } = "Saved PCs";
    public string? Host { get; set; } = "";
    public int Port { get; set; } = 3389;
    public string? UserName { get; set; } = "";
    public string? Domain { get; set; } = "";
    public bool Favorite { get; set; }
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int BitsPerPixel { get; set; } = 24;
    public SecurityMode Security { get; set; } = SecurityMode.NetworkLevelAuthentication;
    public string? CertificateSha256 { get; set; }
    public bool RedirectClipboard { get; set; }
    public bool FitSessionToWindow { get; set; } = true;
    public double LocalZoom { get; set; } = 1;
    public bool UpdateResolutionOnResize { get; set; }
    public ConnectionProfile ToProfile()
    {
        if (Name is null || Group is null || Host is null || UserName is null || Domain is null)
            throw new FormatException("Saved-device text fields cannot be null.");
        return new()
        {
            Id = Id, Name = Name, Group = Group, Host = Host, Port = Port, UserName = UserName,
            Domain = Domain, Favorite = Favorite, Width = Width, Height = Height,
            BitsPerPixel = BitsPerPixel, Security = Security, CertificateSha256 = CertificateSha256,
            RedirectClipboard = RedirectClipboard, FitSessionToWindow = FitSessionToWindow, LocalZoom = LocalZoom,
            UpdateResolutionOnResize = UpdateResolutionOnResize
        };
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(WorkspaceReadDocument))]
internal partial class WorkspaceReadJsonContext : JsonSerializerContext { }
