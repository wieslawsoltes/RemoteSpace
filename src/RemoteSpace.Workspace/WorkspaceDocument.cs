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
    public bool SortDescending { get; init; }
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
/// Single-owner catalog; serialize access to edits. Storage adapters must atomically replace the serialized document; tokens/passwords are never accepted.</summary>
public sealed class DeviceCatalog
{
    public const int MaximumDevices = 10000;
    public const int MaximumDocumentBytes = 16 * 1024 * 1024;
    private WorkspaceDocument _document;
    public DeviceCatalog(WorkspaceDocument? document = null)
    {
        _document = Clone(document ?? new()); Validate(_document);
    }
    private Dictionary<Guid, int>? _indices;
    private SavedDevice[]? _orderedSource;
    private IReadOnlyList<SavedDevice>? _ordered, _query;
    private ProfileSort _orderedSort;
    private bool _orderedDescending;
    private (string Search, bool Favorites, string? Group) _queryKey;
    private IReadOnlyList<string>? _groups;
    public int Count => _document.Devices.Length;
    public long Revision => _document.Revision;
    public WorkspaceSettings Settings => _document.Settings;
    public WorkspaceDocument Snapshot() => Clone(_document);

    // Records and their private array are never mutated. A fork shares the existing
    // snapshot until its first edit; transaction rollback needs no JSON round trip.
    private DeviceCatalog(WorkspaceDocument document, bool validated) => _document = document;
    public DeviceCatalog Fork() => new(_document, true)
    {
        _indices = _indices, _orderedSource = _orderedSource, _ordered = _ordered,
        _orderedSort = _orderedSort, _orderedDescending = _orderedDescending,
        _query = _query, _queryKey = _queryKey, _groups = _groups
    };
    public SavedDevice? Find(Guid id)
    {
        _indices ??= _document.Devices.Select((d, i) => (d.Profile.Id, i)).ToDictionary(p => p.Id, p => p.i);
        return _indices.TryGetValue(id, out int index) ? _document.Devices[index] : null;
    }
    private void Commit(SavedDevice[] devices)
    {
        _document = _document with { Devices = devices, Revision = checked(_document.Revision + 1) };
        _indices = null; _groups = null; _orderedSource = null; _ordered = _query = null;
    }
    public void Upsert(ConnectionProfile profile)
    {
        ValidateProfile(profile);
        var previous = Find(profile.Id);
        if (previous?.Profile == profile) return;
        if (previous is null && Count >= MaximumDevices) throw new InvalidOperationException("Device limit reached.");
        var devices = new SavedDevice[Count + (previous is null ? 1 : 0)];
        _document.Devices.CopyTo(devices, 0);
        if (previous is null) devices[^1] = new(profile);
        else devices[_indices![profile.Id]] = previous with { Profile = profile };
        Commit(devices);
    }
    /// <summary>Validate and merge one bounded import atomically in O(n + m), preserving
    /// existing recency. Duplicate input IDs are rejected; no partial import is committed.</summary>
    public void UpsertRange(IEnumerable<ConnectionProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var devices = _document.Devices.ToList();
        var indices = devices.Select((d, i) => (d.Profile.Id, i)).ToDictionary(p => p.Id, p => p.i);
        var seen = new HashSet<Guid>(); bool changed = false;
        foreach (var profile in profiles)
        {
            if (seen.Count >= MaximumDevices) throw new ArgumentException("Import exceeds the device limit.", nameof(profiles));
            ValidateProfile(profile);
            if (!seen.Add(profile.Id)) throw new ArgumentException("Duplicate device ID in import.", nameof(profiles));
            if (indices.TryGetValue(profile.Id, out int index))
            {
                if (devices[index].Profile != profile) { devices[index] = devices[index] with { Profile = profile }; changed = true; }
            }
            else
            {
                if (devices.Count >= MaximumDevices) throw new InvalidOperationException("Device limit reached.");
                indices.Add(profile.Id, devices.Count); devices.Add(new(profile)); changed = true;
            }
        }
        if (changed) Commit(devices.ToArray());
    }
    public ConnectionProfile Duplicate(Guid id)
    {
        var original = Find(id)?.Profile ?? throw new KeyNotFoundException("Unknown saved device.");
        int length = Math.Min(original.Name.Length, 249);
        if (length < original.Name.Length && char.IsHighSurrogate(original.Name[length - 1])) length--;
        var copy = original with { Id = Guid.NewGuid(), Name = original.Name[..length] + " (copy)", Favorite = false };
        Upsert(copy); return copy;
    }
    public bool Remove(Guid id)
    {
        if (Find(id) is null) return false;
        Commit(_document.Devices.Where(d => d.Profile.Id != id).ToArray()); return true;
    }
    public void MarkConnected(Guid id, DateTimeOffset time)
    {
        var previous = Find(id) ?? throw new KeyNotFoundException("Unknown saved device.");
        if (previous.LastConnected == time) return;
        var devices = (SavedDevice[])_document.Devices.Clone();
        devices[_indices![id]] = previous with { LastConnected = time }; Commit(devices);
    }
    public void Configure(WorkspaceSettings settings)
    {
        ValidateSettings(settings);
        if (settings != _document.Settings) _document = _document with { Settings = settings, Revision = checked(_document.Revision + 1) };
    }
    public IReadOnlyList<string> Groups => _groups ??= Array.AsReadOnly(_document.Devices.Select(d => d.Profile.Group)
        .Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ThenBy(g => g, StringComparer.Ordinal).ToArray());
    private IReadOnlyList<SavedDevice> Ordered()
    {
        var settings = Settings;
        if (_ordered is not null && ReferenceEquals(_orderedSource, _document.Devices) && _orderedSort == settings.Sort && _orderedDescending == settings.SortDescending) return _ordered;
        IEnumerable<SavedDevice> items = _document.Devices;
        IOrderedEnumerable<SavedDevice> sorted = settings.Sort switch
        {
            ProfileSort.Group => items.OrderBy(d => d.Profile.Group, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Profile.Name, StringComparer.OrdinalIgnoreCase),
            ProfileSort.RecentlyUsed => items.OrderByDescending(d => d.LastConnected).ThenBy(d => d.Profile.Name, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderBy(d => d.Profile.Name, StringComparer.OrdinalIgnoreCase)
        };
        var ordered = sorted.ThenBy(d => d.Profile.Id).ToArray();
        if (settings.SortDescending) Array.Reverse(ordered);
        _orderedSource = _document.Devices; _orderedSort = settings.Sort; _orderedDescending = settings.SortDescending;
        _query = null; return _ordered = Array.AsReadOnly(ordered);
    }
    public IReadOnlyList<SavedDevice> Query(string search = "", bool favoritesOnly = false, string? group = null)
    {
        ArgumentNullException.ThrowIfNull(search);
        var ordered = Ordered(); var key = (search, favoritesOnly, group);
        if (_query is not null && _queryKey == key) return _query;
        _queryKey = key;
        // Filtering the cached order avoids sorting on every keystroke. Only one
        // last-result cache is retained, so arbitrary searches cannot grow memory.
        return _query = search.Length == 0 && !favoritesOnly && group is null ? ordered :
            Filter(ordered, search, favoritesOnly, group);
    }
    private static IReadOnlyList<SavedDevice> Filter(IReadOnlyList<SavedDevice> ordered, string search, bool favoritesOnly, string? group) =>
            Array.AsReadOnly(ordered.Where(d => (!favoritesOnly || d.Profile.Favorite) && (group is null || d.Profile.Group == group) &&
                (d.Profile.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || d.Profile.Host.Contains(search, StringComparison.OrdinalIgnoreCase) || d.Profile.Group.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray());
    public string Serialize()
    {
        string json = JsonSerializer.Serialize(_document, WorkspaceJsonContext.Default.WorkspaceDocument);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
            throw new FormatException("The workspace exceeds the storage limit. Remove devices or shorten their fields before saving.");
        return json;
    }
    public static DeviceCatalog Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes) throw new FormatException("Workspace exceeds the storage limit.");
        var doc = JsonSerializer.Deserialize(json.AsSpan().TrimStart('\ufeff'), WorkspaceJsonContext.Default.WorkspaceDocument) ?? throw new FormatException("Empty workspace.");
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
