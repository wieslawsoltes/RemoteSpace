using RemoteSpace.Workspace;
using Windows.Storage;

namespace RemoteSpace.App;

internal sealed class WorkspaceStore
{
    private readonly SemaphoreSlim _gate = new(1);
    private long? _revision;
#if __WASM__
    private string? _browserDocument;
#endif
    private const string Name = "workspace.json";
    public async Task<DeviceCatalog> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
#if __WASM__
            string stored = await BrowserWorkspaceStore.ReadAsync();
            _browserDocument = stored.Length == 0 ? null : stored;
            if (_browserDocument is not null)
            {
                var current = DeviceCatalog.Parse(_browserDocument);
                _revision = current.Snapshot().Revision; return current;
            }
#endif
            var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(Name) as StorageFile;
            var catalog = file is null ? new DeviceCatalog() : DeviceCatalog.Parse(await FileIO.ReadTextAsync(file));
#if __WASM__
            // Migrate an older filesystem-backed workspace without overwriting a new browser record.
            if (file is not null)
            {
                string migrated = catalog.Serialize();
                await BrowserWorkspaceStore.ReplaceAsync(null, migrated); _browserDocument = migrated;
            }
#endif
            _revision = catalog.Snapshot().Revision; return catalog;
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(DeviceCatalog catalog)
    {
        await _gate.WaitAsync();
        try
        {
            if (_revision is null) throw new InvalidOperationException("The existing workspace could not be loaded. It has not been overwritten.");
#if __WASM__
            string next = catalog.Serialize();
            await BrowserWorkspaceStore.ReplaceAsync(_browserDocument, next);
            _browserDocument = next; _revision = catalog.Snapshot().Revision;
#else
            var folder = ApplicationData.Current.LocalFolder;
            var existing = await folder.TryGetItemAsync(Name) as StorageFile;
            if (existing is not null && DeviceCatalog.Parse(await FileIO.ReadTextAsync(existing)).Snapshot().Revision != _revision)
                throw new IOException("Saved devices changed in another window. Reload before saving; the other window's data was preserved.");
            var temporary = await folder.CreateFileAsync("workspace-" + Guid.NewGuid().ToString("N") + ".tmp", CreationCollisionOption.FailIfExists);
            try
            {
                await FileIO.WriteTextAsync(temporary, catalog.Serialize());
                if (existing is null) await temporary.RenameAsync(Name, NameCollisionOption.FailIfExists);
                else await temporary.MoveAndReplaceAsync(existing);
                _revision = catalog.Snapshot().Revision;
            }
            catch { try { await temporary.DeleteAsync(); } catch { } throw; }
#endif
        }
        finally { _gate.Release(); }
    }
}
