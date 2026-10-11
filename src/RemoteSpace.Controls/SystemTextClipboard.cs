using Windows.ApplicationModel.DataTransfer;
#if __WASM__
using System.Runtime.InteropServices.JavaScript;
using System.Text;
#endif

namespace RemoteSpace.Controls;

/// <summary>UI-thread local text clipboard access. Call only from an explicit user command.
/// InitializeAsync loads interop without reading or changing the clipboard. Browser operations
/// await the actual platform promise and report denied access rather than a successful no-op.</summary>
public interface ITextClipboard
{
    Task InitializeAsync();
    Task<string?> ReadTextAsync();
    Task WriteTextAsync(string text);
}

public sealed partial class SystemTextClipboard : ITextClipboard
{
    public Task InitializeAsync()
    {
#if __WASM__
        return Module;
#else
        return Task.CompletedTask;
#endif
    }
    public async Task<string?> ReadTextAsync()
    {
#if __WASM__
        EnsureReady();
        return await ReadBrowserText();
#else
        var content = Clipboard.GetContent();
        return content is not null && content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : null;
#endif
    }
    public async Task WriteTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
#if __WASM__
        EnsureReady();
        await WriteBrowserText(text);
#else
        var content = new DataPackage(); content.SetText(text); Clipboard.SetContent(content);
        await Task.CompletedTask;
#endif
    }
#if __WASM__
    private static readonly Task<JSObject> Module = JSHost.ImportAsync("remotespace.clipboard",
        "data:text/javascript;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(Source)));
    private static void EnsureReady()
    {
        // Do not defer a permission-sensitive operation until after loading a module:
        // that can lose transient user activation. Hosts initialize this service on load.
        if (!Module.IsCompletedSuccessfully) throw new InvalidOperationException("Clipboard access is not initialized. Reload the application and retry the command.");
    }
    [JSImport("readText", "remotespace.clipboard")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string> ReadBrowserText();
    [JSImport("writeText", "remotespace.clipboard")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string> WriteBrowserText(string text);
    private const string Source = """
function clipboard() {
    if (!globalThis.isSecureContext || !globalThis.navigator?.clipboard)
        throw new Error('Clipboard access requires a secure browser context and a supported browser.');
    if (!document.hasFocus()) throw new Error('Focus the RemoteSpace window and retry the clipboard command.');
    return navigator.clipboard;
}
export async function readText() {
    const platform = clipboard();
    try { return await platform.readText(); }
    catch { throw new Error('Clipboard read was denied or failed. Allow clipboard access for this site, then retry Send clipboard.'); }
}
export async function writeText(text) {
    const platform = clipboard();
    try { await platform.writeText(text); return 'written'; }
    catch { throw new Error('Clipboard write was denied or failed. Allow clipboard access for this site, then retry Copy remote text.'); }
}
""";
#endif
}
