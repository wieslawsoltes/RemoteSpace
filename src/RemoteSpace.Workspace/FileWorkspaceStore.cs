using System.Text;
using RemoteSpace.Core;

namespace RemoteSpace.Workspace;

/// <summary>Native, credential-free storage for cooperating processes. A persistent
/// exclusive lock file serializes compare-and-replace; the old complete file remains
/// until a bounded UTF-8 replacement has been written and flushed in the same directory.
/// External writers that ignore this lock, network filesystems and disk failure are
/// outside this coordination contract. Browser hosts should use their IndexedDB adapter.</summary>
public sealed class FileWorkspaceStore
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string _path, _lock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;
    private string? _previous;
    public FileWorkspaceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path); _lock = _path + ".lock";
    }
    public async Task<DeviceCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _loaded = false;
            await using var held = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            string? text = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var catalog = text is null ? new DeviceCatalog() : DeviceCatalog.Parse(text);
            _previous = text; _loaded = true; return catalog;
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(DeviceCatalog catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        // Snapshot serialization is completed before any on-disk mutation.
        string next = catalog.Serialize();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded) throw new InvalidOperationException("Load the existing workspace successfully before saving. Existing data was preserved.");
            await using var held = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            string? current = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, _previous, StringComparison.Ordinal))
                throw new IOException("Saved devices changed in another window. Reload before saving; the other window's data was preserved.");
            if (string.Equals(current, next, StringComparison.Ordinal)) return;
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = Utf8.GetBytes(next);
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    file.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, _path, overwrite: true);
                _previous = next;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }
    private async Task<string?> ReadAsync(CancellationToken ct)
    {
        try
        {
            await using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytes = await BoundedInput.ReadAsync(file, DeviceCatalog.MaximumDocumentBytes, ct).ConfigureAwait(false);
            // Accept a legacy UTF-8 BOM without changing exact-document comparison.
            return Utf8.GetString(bytes);
        }
        catch (FileNotFoundException) { return null; }
    }
    private async Task<FileStream> AcquireAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { return new FileStream(_lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
            catch (IOException)
            {
                await Task.Delay(25, deadline.Token).ConfigureAwait(false);
            }
        }
    }
}
