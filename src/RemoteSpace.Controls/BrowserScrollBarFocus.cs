using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
#if __WASM__
using System.Runtime.InteropServices.JavaScript;
using System.Text;
#endif

namespace RemoteSpace.Controls;

/// <summary>Keyboard compatibility for owned scrollbars in the pinned Uno Skia
/// browser host. Navigation is applied to the registered native control, not to
/// whichever control a delayed focus notification leaves selected. Other local
/// keys keep browser defaults without entering remote keyboard routing. No private
/// host API or DOM value mutation is used.</summary>
public static partial class BrowserScrollBarFocus
{
    public static Task<IDisposable> RegisterAsync(ScrollBar bar) => RegisterAsync(bar, null);

    /// <param name="localInput">Optional UI-owner notification to release tracked
    /// remote input when a keyboard gesture belongs to this local scrollbar.</param>
    public static async Task<IDisposable> RegisterAsync(ScrollBar bar, Action? localInput)
    {
        ArgumentNullException.ThrowIfNull(bar);
        if (!bar.DispatcherQueue.HasThreadAccess)
            throw new InvalidOperationException("Register scrollbar focus on its UI thread.");
#if __WASM__
        await Module;
        string id = AutomationProperties.GetAutomationId(bar);
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("A registered scrollbar needs a unique AutomationId.", nameof(bar));
        Register(id, bar.Orientation == Orientation.Horizontal, key =>
        {
            if (!bar.IsLoaded || !bar.IsEnabled || bar.Visibility != Visibility.Visible) return;
            // DOM focus already identifies the intended owner. Do not request
            // native focus here: its asynchronous reply can steal focus from the
            // next scrollbar before that scrollbar receives its physical key.
            // Navigation changes the owned native range, never DOM values.
            localInput?.Invoke();
            // Zero denotes a local non-navigation key: leave its browser default
            // intact, but do not let the global XAML router send it to RDP.
            if (key != 0) ScrollBarNavigation.Apply(bar, (VirtualKey)key);
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
    private static partial void Register(string id, bool horizontal, [JSMarshalAs<JSType.Function<JSType.Number>>] Action<int> navigate);
    [JSImport("unregister", "remotespace.scrollbar-focus")]
    private static partial void Unregister(string id);
    private const string Source = """
const commonKeys = new Map([['Home',36],['End',35],['PageUp',33],['PageDown',34]]);
const horizontalKeys = new Map([['ArrowLeft',37],['ArrowRight',39]]);
const verticalKeys = new Map([['ArrowUp',38],['ArrowDown',40]]);
export function bind(root, registrations) {
    let dispatching = false, disposed = false;
    const navigate = event => {
        const target = event.target;
        if (disposed || dispatching || event.defaultPrevented ||
            target !== root.ownerDocument.activeElement || target?.getAttribute?.('role') !== 'scrollbar' ||
            target.getAttribute('aria-disabled') === 'true') return;
        const registration = registrations.get(target.getAttribute('xamlautomationid'));
        if (!registration) return;
        const modified = event.ctrlKey || event.altKey || event.metaKey;
        const key = modified ? undefined :
            commonKeys.get(event.key) ?? (registration.horizontal ? horizontalKeys : verticalKeys).get(event.key);
        // All keyboard gestures on an owned local scrollbar stay local. Preserve
        // browser defaults for Tab, text and modified shortcuts, but never route
        // them to the native remote surface merely because XAML focus is stale.
        event.stopPropagation();
        if (key !== undefined) event.preventDefault();
        if (event.type === 'keyup') return;
        dispatching = true;
        try { registration.navigate(key ?? 0); } finally { dispatching = false; }
    };
    root.addEventListener('keydown', navigate);
    root.addEventListener('keyup', navigate);
    return () => {
        disposed = true;
        root.removeEventListener('keydown', navigate);
        root.removeEventListener('keyup', navigate);
    };
}
const registrations = new Map();
let installedRoot, unbind;
export function register(id, horizontal, navigate) {
    if (!id || typeof horizontal !== 'boolean' || typeof navigate !== 'function') throw new Error('Invalid scrollbar navigation registration.');
    if (registrations.has(id)) throw new Error('Duplicate scrollbar navigation registration.');
    if (registrations.size >= 64) throw new Error('Scrollbar navigation registration limit exceeded.');
    const root = document.getElementById('uno-semantics-root');
    if (!root) throw new Error('The Uno semantic root is not ready.');
    if (root !== installedRoot) {
        unbind?.(); unbind = bind(root, registrations); installedRoot = root;
    }
    registrations.set(id, {horizontal, navigate});
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
