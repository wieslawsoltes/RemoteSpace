using RemoteSpace.Workspace;
using Windows.Storage;

namespace RemoteSpace.App;

internal sealed class WorkspaceStore
{
    private readonly SemaphoreSlim _gate = new(1);
    private long? _revision;
    private const string Name = "workspace.json";
    public async Task<DeviceCatalog> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(Name) as StorageFile;
            var catalog = file is null ? new DeviceCatalog() : DeviceCatalog.Parse(await FileIO.ReadTextAsync(file));
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
        }
        finally { _gate.Release(); }
    }
}
