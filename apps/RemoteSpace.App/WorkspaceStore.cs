using RemoteSpace.Workspace;
using Windows.Storage;

namespace RemoteSpace.App;

internal sealed class WorkspaceStore
{
#if !__WASM__
    private FileWorkspaceStore? _native;
    private FileWorkspaceStore Native => _native ??= new(Path.Combine(ApplicationData.Current.LocalFolder.Path, "workspace.json"));
    public Task<DeviceCatalog> LoadAsync() => Native.LoadAsync();
    public Task SaveAsync(DeviceCatalog catalog) => Native.SaveAsync(catalog);
#else
    private readonly SemaphoreSlim _gate = new(1);
    private long? _revision;
    private string? _browserDocument;
    private const string Name = "workspace.json";
    public async Task<DeviceCatalog> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _revision = null; // A failed reload cannot authorize overwriting corrupt data.
            string stored = await BrowserWorkspaceStore.ReadAsync();
            _browserDocument = stored.Length == 0 ? null : stored;
            if (_browserDocument is not null)
            {
                var current = DeviceCatalog.Parse(_browserDocument);
                _revision = current.Revision; return current;
            }
            var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(Name) as StorageFile;
            var catalog = file is null ? new DeviceCatalog() : DeviceCatalog.Parse(await FileIO.ReadTextAsync(file));
            // Migrate an older filesystem-backed workspace without overwriting a new browser record.
            if (file is not null)
            {
                string migrated = catalog.Serialize();
                await BrowserWorkspaceStore.ReplaceAsync(null, migrated); _browserDocument = migrated;
            }
            _revision = catalog.Revision; return catalog;
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(DeviceCatalog catalog)
    {
        await _gate.WaitAsync();
        try
        {
            if (_revision is null) throw new InvalidOperationException("The existing workspace could not be loaded. It has not been overwritten.");
            string next = catalog.Serialize();
            await BrowserWorkspaceStore.ReplaceAsync(_browserDocument, next);
            _browserDocument = next; _revision = catalog.Revision;
        }
        finally { _gate.Release(); }
    }
#endif
}
