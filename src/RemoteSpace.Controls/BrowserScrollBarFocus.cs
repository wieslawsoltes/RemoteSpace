using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
#if __WASM__
using System.Runtime.InteropServices.JavaScript;
using System.Text;
#endif

namespace RemoteSpace.Controls;

/// <summary>Focus compatibility for the pinned Uno Skia browser host's generic
/// scrollbar peers. Bridges only registered scrollbar focus to the public native
/// Focus API. No key, value, geometry or private runtime API is intercepted.</summary>
public static partial class BrowserScrollBarFocus
{
    public static async Task<IDisposable> RegisterAsync(ScrollBar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        if (!bar.DispatcherQueue.HasThreadAccess)
            throw new InvalidOperationException("Register scrollbar focus on its UI thread.");
#if __WASM__
        await Module;
        string id = AutomationProperties.GetAutomationId(bar);
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("A registered scrollbar needs a unique AutomationId.", nameof(bar));
        Register(id, () =>
        {
            if (bar.IsLoaded && bar.IsEnabled && bar.Visibility == Visibility.Visible)
                bar.Focus(FocusState.Keyboard);
        });
        return new Registration(id);
#else
        await Task.CompletedTask;
        return new Registration();
#endif
    }

    private sealed class Registration : IDisposable
    {
#if __WASM__
        private string? _id;
        public Registration(string id) => _id = id;
        public void Dispose()
        {
            if (_id is not { } id) return;
            _id = null; Unregister(id);
        }
#else
        public void Dispose() { }
#endif
    }
#if __WASM__
    private static readonly Task<JSObject> Module = JSHost.ImportAsync("remotespace.scrollbar-focus",
        "data:text/javascript;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(Source)));
    [JSImport("register", "remotespace.scrollbar-focus")]
    private static partial void Register(string id, [JSMarshalAs<JSType.Function>] Action focus);
    [JSImport("unregister", "remotespace.scrollbar-focus")]
    private static partial void Unregister(string id);
    private const string Source = """
export function bind(root, registrations) {
    let dispatching = false;
    const focus = event => {
        const target = event.target;
        if (dispatching || target?.getAttribute?.('role') !== 'scrollbar') return;
        const callback = registrations.get(target.getAttribute('xamlautomationid'));
        if (!callback) return;
        dispatching = true;
        try { callback(); } finally { dispatching = false; }
    };
    root.addEventListener('focusin', focus);
    return () => root.removeEventListener('focusin', focus);
}
const registrations = new Map();
let installedRoot, unbind;
export function register(id, focus) {
    if (!id || typeof focus !== 'function') throw new Error('Invalid scrollbar focus registration.');
    if (registrations.has(id)) throw new Error('Duplicate scrollbar focus registration.');
    if (registrations.size >= 64) throw new Error('Scrollbar focus registration limit exceeded.');
    const root = document.getElementById('uno-semantics-root');
    if (!root) throw new Error('The Uno semantic root is not ready.');
    if (root !== installedRoot) {
        unbind?.(); unbind = bind(root, registrations); installedRoot = root;
    }
    registrations.set(id, focus);
}
export function unregister(id) {
    registrations.delete(id);
    if (registrations.size === 0) {
        unbind?.(); unbind = installedRoot = undefined;
    }
}
""";
#endif
}
