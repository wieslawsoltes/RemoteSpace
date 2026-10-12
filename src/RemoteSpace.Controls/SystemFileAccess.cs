using RemoteSpace.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
#if __WASM__
using System.Runtime.InteropServices.JavaScript;
using System.Text;
#endif

namespace RemoteSpace.Controls;

public sealed record SelectedLocalFile(string Name, byte[] Bytes);

/// <summary>User-initiated local import/export, not remote drive or clipboard-file redirection.
/// Browser export requests a download; it cannot report completion of a disk save.</summary>
public interface IUserFileAccess
{
    Task InitializeAsync();
    Task<SelectedLocalFile?> PickAsync(string extension, int maximumBytes);
    Task<bool> ExportAsync(string name, byte[] bytes, string mimeType);
}

public sealed partial class SystemFileAccess : IUserFileAccess
{
    public const int MaximumBytes = 64 * 1024 * 1024;
    public Task InitializeAsync()
    {
#if __WASM__
        return Module;
#else
        return Task.CompletedTask;
#endif
    }
    public async Task<SelectedLocalFile?> PickAsync(string extension, int maximumBytes)
    {
        ValidateExtension(extension);
        if (maximumBytes < 0 || maximumBytes > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
#if __WASM__
        EnsureReady();
        using var result = await PickBrowserFile(extension, maximumBytes);
        if (result.GetPropertyAsBoolean("cancelled")) return null;
        var bytes = result.GetPropertyAsByteArray("bytes") ?? throw new InvalidDataException("The selected file could not be read.");
        if (bytes.Length > maximumBytes) throw new InvalidDataException("The selected file exceeds the byte limit.");
        return new(result.GetPropertyAsString("name") ?? "Imported file", bytes);
#else
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(extension);
        var file = await picker.PickSingleFileAsync(); if (file is null) return null;
        if ((await file.GetBasicPropertiesAsync()).Size > (ulong)maximumBytes) throw new InvalidDataException("The selected file exceeds the byte limit.");
        using var stream = await file.OpenStreamForReadAsync();
        return new(file.Name, await BoundedInput.ReadAsync(stream, maximumBytes));
#endif
    }
    public async Task<bool> ExportAsync(string name, byte[] bytes, string mimeType)
    {
        ArgumentNullException.ThrowIfNull(bytes); ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("The export exceeds the 64 MiB limit.");
        string extension = Path.GetExtension(name); ValidateExtension(extension);
        string stem = name[..^extension.Length];
        int length = Math.Min(stem.Length, 240 - extension.Length);
        if (length > 0 && char.IsHighSurrogate(stem[length - 1])) length--;
        stem = string.Concat(stem.AsSpan(0, length).ToString().Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c)).TrimEnd('.', ' ');
        name = (stem.Length == 0 ? "RemoteSpace" : stem) + extension;
        if (mimeType is not ("application/json" or "application/octet-stream" or "image/png" or "text/plain")) throw new ArgumentException("Unsupported export MIME type.", nameof(mimeType));
#if __WASM__
        EnsureReady(); DownloadBrowserFile(name, bytes, mimeType);
        await Task.CompletedTask; return true;
#else
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(name) };
        picker.FileTypeChoices.Add("RemoteSpace file", new List<string> { extension });
        var file = await picker.PickSaveFileAsync(); if (file is null) return false;
        await FileIO.WriteBytesAsync(file, bytes); return true;
#endif
    }
    private static void ValidateExtension(string extension)
    {
        if (extension is null || extension.Length is < 2 or > 12 || extension[0] != '.' || !extension.AsSpan(1).ToString().All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Supply a single file extension.", nameof(extension));
    }
#if __WASM__
    private static readonly Task<JSObject> Module = JSHost.ImportAsync("remotespace.files",
        "data:text/javascript;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(Source)));
    private static void EnsureReady()
    {
        if (!Module.IsCompletedSuccessfully) throw new InvalidOperationException("Local file access is not initialized. Reload the application and retry.");
    }
    [JSImport("pick", "remotespace.files")]
    [return: JSMarshalAs<JSType.Promise<JSType.Object>>]
    private static partial Task<JSObject> PickBrowserFile(string extension, int maximumBytes);
    [JSImport("download", "remotespace.files")]
    private static partial void DownloadBrowserFile(string name, [JSMarshalAs<JSType.Array<JSType.Number>>] byte[] bytes, string mimeType);
    private const string Source = """
let activeSelection = null;
const downloads = new Map();
let downloadBytes = 0;
export function pick(extension, maximumBytes) {
    if (activeSelection) throw new Error('A file selection is already open. Complete or cancel it first.');
    if (!/^\.[a-z0-9]{1,11}$/i.test(extension) || !Number.isInteger(maximumBytes) || maximumBytes < 0 || maximumBytes > 67108864)
        throw new Error('Invalid local file selection limits.');
    return new Promise((resolve, reject) => {
        let input, reader, timer, finished = false, reading = false;
        const finish = (result, error) => {
            if (finished) return;
            finished = true;
            if (activeSelection === cancel) activeSelection = null;
            if (timer !== undefined) clearTimeout(timer);
            if (input) {
                input.onchange = input.oncancel = null;
                // Complete even if a host tears down the DOM during navigation.
                try { input.remove(); } catch { }
            }
            if (reader) {
                reader.onload = reader.onerror = reader.onabort = null;
                try { if (reader.readyState === 1) reader.abort(); } catch { }
            }
            if (error) reject(error); else resolve(result);
        };
        const cancel = () => finish({cancelled:true});
        activeSelection = cancel;
        // Every setup step belongs to this operation, including creation and
        // timeout setup. A failure must never leave the single-picker guard set.
        try {
            input = document.createElement('input');
            input.type = 'file'; input.accept = extension; input.style.display = 'none';
            timer = setTimeout(() => finish(null, new Error('File selection timed out. Retry the import command.')), 300000);
            input.oncancel = cancel;
            input.onchange = () => {
                if (finished || reading) return;
                reading = true;
                try {
                    const file = input.files?.[0];
                    if (!file) { cancel(); return; }
                    if (!file.name.toLowerCase().endsWith(extension.toLowerCase())) throw new Error('The selected file has the wrong extension.');
                    if (!Number.isSafeInteger(file.size) || file.size < 0 || file.size > maximumBytes)
                        throw new Error('The selected file exceeds the byte limit.');
                    reader = new FileReader();
                    reader.onload = () => {
                        if (finished) return;
                        try {
                            if (!(reader.result instanceof ArrayBuffer)) throw new Error('The selected file could not be read.');
                            const bytes = new Uint8Array(reader.result);
                            if (bytes.length > maximumBytes) throw new Error('The selected file exceeds the byte limit.');
                            finish({cancelled:false, name:file.name, bytes});
                        } catch (error) { finish(null, error); }
                    };
                    reader.onerror = () => finish(null, new Error('The selected file could not be read. Retry the import command.'));
                    reader.onabort = cancel;
                    // FileReader can actually abort an in-progress read. Slice
                    // bounds the materialized range before allocating its result;
                    // the extra byte detects a size mismatch without a full read.
                    reader.readAsArrayBuffer(file.slice(0, maximumBytes + 1));
                } catch (error) { finish(null, error); }
            };
            document.body.append(input); input.click();
        } catch (error) { finish(null, error); }
    });
}
function release(url) {
    const download = downloads.get(url);
    if (download === undefined) return;
    downloads.delete(url); downloadBytes -= download.size;
    if (download.timer !== undefined) clearTimeout(download.timer);
    URL.revokeObjectURL(url);
}
export function download(name, bytes, mimeType) {
    if (!(bytes instanceof Uint8Array) || bytes.length > 67108864) throw new Error('The export exceeds its byte limit.');
    if (downloads.size >= 4 || downloadBytes + bytes.length > 67108864) throw new Error('Previous downloads are still being prepared. Retry after they start.');
    const url = URL.createObjectURL(new Blob([bytes], {type:mimeType}));
    const download = {size:bytes.length, timer:undefined};
    downloads.set(url, download); downloadBytes += bytes.length;
    let link;
    try {
        try {
            link = document.createElement('a'); link.href = url; link.download = name;
            document.body.append(link); link.click();
        } finally { link?.remove(); }
        // A synchronous pagehide during a host click may already have released
        // this URL. Do not resurrect its retention timer after that cleanup.
        if (downloads.has(url)) download.timer = setTimeout(() => release(url), 30000);
    } catch (error) { release(url); throw error; }
}
globalThis.addEventListener('pagehide', () => {
    activeSelection?.();
    for (const url of downloads.keys()) release(url);
});
""";
#endif
}
