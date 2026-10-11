using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteSpace.Profiles;

namespace RemoteSpace.Workspace;

public enum ProfileSort { Name, Group, RecentlyUsed }
public enum WorkspaceTheme { System, Light, Dark }
public sealed record WorkspaceSettings
{
    public WorkspaceTheme Theme { get; init; }
    public ProfileSort Sort { get; init; }
    public bool ListView { get; init; }
    public string BridgeEndpoint { get; init; } = "";
}
public sealed record SavedDevice(ConnectionProfile Profile, DateTimeOffset? LastConnected = null);
public sealed record WorkspaceDocument
{
    public int SchemaVersion { get; init; } = 1;
    public long Revision { get; init; }
    public WorkspaceSettings Settings { get; init; } = new();
    public SavedDevice[] Devices { get; init; } = [];
}
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WorkspaceDocument))]
public partial class WorkspaceJsonContext : JsonSerializerContext { }

/// <summary>Credential-free, versioned workspace with immutable snapshots and bounded import.
/// Storage adapters must atomically replace the serialized document; tokens/passwords are never accepted.</summary>
public sealed class DeviceCatalog
{
    public const int MaximumDevices = 10000;
    public const int MaximumDocumentBytes = 16 * 1024 * 1024;
    private WorkspaceDocument _document;
    public DeviceCatalog(WorkspaceDocument? document = null)
    {
        _document = Clone(document ?? new()); Validate(_document);
    }
    public WorkspaceDocument Snapshot() => Clone(_document);
    public void Upsert(ConnectionProfile profile)
    {
        ValidateProfile(profile);
        var devices = _document.Devices.ToList(); int index = devices.FindIndex(d => d.Profile.Id == profile.Id);
        if (index >= 0) devices[index] = devices[index] with { Profile = profile };
        else { if (devices.Count >= MaximumDevices) throw new InvalidOperationException("Device limit reached."); devices.Add(new(profile)); }
        _document = _document with { Devices = devices.ToArray(), Revision = checked(_document.Revision + 1) };
    }
    public bool Remove(Guid id)
    {
        var devices = _document.Devices.Where(d => d.Profile.Id != id).ToArray();
        if (devices.Length == _document.Devices.Length) return false;
        _document = _document with { Devices = devices, Revision = checked(_document.Revision + 1) }; return true;
    }
    public void MarkConnected(Guid id, DateTimeOffset time)
    {
        if (!_document.Devices.Any(d => d.Profile.Id == id)) throw new KeyNotFoundException("Unknown saved device.");
        _document = _document with { Devices = _document.Devices.Select(d => d.Profile.Id == id ? d with { LastConnected = time } : d).ToArray(), Revision = checked(_document.Revision + 1) };
    }
    public void Configure(WorkspaceSettings settings)
    {
        ValidateSettings(settings); _document = _document with { Settings = settings, Revision = checked(_document.Revision + 1) };
    }
    public IReadOnlyList<SavedDevice> Query(string search = "", bool favoritesOnly = false, string? group = null)
    {
        ArgumentNullException.ThrowIfNull(search);
        IEnumerable<SavedDevice> items = _document.Devices.Where(d => (!favoritesOnly || d.Profile.Favorite) &&
            (group is null || d.Profile.Group == group) &&
            (d.Profile.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || d.Profile.Host.Contains(search, StringComparison.OrdinalIgnoreCase) || d.Profile.Group.Contains(search, StringComparison.OrdinalIgnoreCase)));
        return (_document.Settings.Sort switch
        {
            ProfileSort.Group => items.OrderBy(d => d.Profile.Group, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Profile.Name, StringComparer.OrdinalIgnoreCase),
            ProfileSort.RecentlyUsed => items.OrderByDescending(d => d.LastConnected).ThenBy(d => d.Profile.Name, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderBy(d => d.Profile.Name, StringComparer.OrdinalIgnoreCase)
        }).ThenBy(d => d.Profile.Id).ToArray();
    }
    public string Serialize() => JsonSerializer.Serialize(_document, WorkspaceJsonContext.Default.WorkspaceDocument);
    public static DeviceCatalog Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes) throw new FormatException("Workspace exceeds the storage limit.");
        var doc = JsonSerializer.Deserialize(json, WorkspaceJsonContext.Default.WorkspaceDocument) ?? throw new FormatException("Empty workspace.");
        return new(doc);
    }
    private static WorkspaceDocument Clone(WorkspaceDocument doc) => doc with { Devices = doc.Devices is null ? throw new FormatException("Missing device list.") : (SavedDevice[])doc.Devices.Clone() };
    private static void Validate(WorkspaceDocument doc)
    {
        if (doc.SchemaVersion != 1) throw new FormatException("Unsupported workspace schema; existing data was not overwritten.");
        if (doc.Revision < 0 || doc.Devices.Length > MaximumDevices || doc.Devices.Any(d => d is null)) throw new FormatException("Invalid workspace data.");
        ValidateSettings(doc.Settings);
        var ids = new HashSet<Guid>();
        foreach (var device in doc.Devices) { ValidateProfile(device.Profile); if (!ids.Add(device.Profile.Id)) throw new FormatException("Duplicate device ID."); }
    }
    private static void ValidateSettings(WorkspaceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.Theme) || !Enum.IsDefined(settings.Sort)) throw new FormatException("Invalid workspace preference.");
        if (settings.BridgeEndpoint is null) throw new FormatException("Missing bridge endpoint preference.");
        if (settings.BridgeEndpoint.Length > 0)
        {
            if (!Uri.TryCreate(settings.BridgeEndpoint, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
                (uri.Scheme != "wss" && !(uri.Scheme == "ws" && uri.IsLoopback)))
                throw new FormatException("Use a wss:// bridge endpoint without secrets or query parameters; ws:// is limited to loopback.");
        }
    }
    private static void ValidateProfile(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile); profile.ToOptions().Validate();
        if (profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 256 || profile.Group is null || profile.Group.Length > 256 ||
            profile.Name.Any(char.IsControl) || profile.Group.Any(char.IsControl) || profile.UserName is null || profile.Domain is null || profile.UserName.Length > 255 || profile.Domain.Length > 255 || profile.UserName.Contains('\0') || profile.Domain.Contains('\0'))
            throw new ArgumentException("Invalid device name, group, ID or account fields.", nameof(profile));
    }
}
