#if __WASM__
using System.Runtime.InteropServices.JavaScript;
using System.Text;
#endif

namespace RemoteSpace.Controls;

/// <summary>Compatibility boundary for the pinned Uno Skia browser host. A semantic
/// menu invokes on keydown and cancels the DOM default, but the host also routes that
/// already-handled event into native XAML after focus has changed. Consume only that
/// menu gesture before document routing. No DOM values, private Uno APIs or event
/// providers are replaced. Native hosts require no adaptation.</summary>
public static partial class BrowserSemanticInput
{
    /// <summary>Only the adapter-owned Enter/Space gestures. This is queried by
    /// an active release wait, never by an idle timer. Native hosts continue to
    /// use the platform key state; no remote/text input is exposed here.</summary>
    internal static bool IsActivationHeld
    {
        get
        {
#if __WASM__
            return _ready && ReadActivationHeld();
#else
            return false;
#endif
        }
    }
    public static async Task InitializeAsync()
    {
#if __WASM__
        await Module;
        Install(); _ready = true;
#else
        await Task.CompletedTask;
#endif
    }
#if __WASM__
    private static bool _ready;
    private static readonly Task<JSObject> Module = JSHost.ImportAsync("remotespace.semantic-input",
        "data:text/javascript;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(Source)));
    [JSImport("install", "remotespace.semantic-input")]
    private static partial void Install();
    [JSImport("isActivationHeld", "remotespace.semantic-input")]
    private static partial bool ReadActivationHeld();
    private const string Source = """
let installedRoot, dispose;
export function bind(root, host) {
    // At most three physical activation keys. No arbitrary key values or user
    // input contents are retained; no polling, timers or mutation observers.
    const held = new Set();
    const buttonKeys = new Map();
    const activation = e => e.key === 'Enter'
        ? (e.code === 'NumpadEnter' ? 'NumpadEnter' : 'Enter')
        : e.key === ' ' ? 'Space' : null;
    const stop = e => { e.preventDefault(); e.stopPropagation(); };
    const down = e => {
        const key = activation(e);
        if (!key) return;
        const button = e.composedPath().find(n => n?.tagName === 'BUTTON' &&
            n.getAttribute?.('xamlautomationid')?.startsWith('remotespace-session-command-'));
        if (button && !e.defaultPrevented) {
            // A native HTML button already implements Enter/Space and invokes
            // Uno's existing provider on click. Keep that default, but do not
            // also route the same key to XAML's possibly different focused item.
            buttonKeys.set(key, button);
            e.stopPropagation();
            return;
        }
        if (!e.defaultPrevented) return;
        // A callback may already have removed its menu. The dispatch path, not
        // current DOM ancestry, preserves the scope that handled this event.
        const path = e.composedPath();
        if (!path.includes(root) || !path.some(n => n?.getAttribute?.('role') === 'menuitem')) return;
        held.add(key);
        stop(e);
    };
    const repeat = e => {
        const key = activation(e);
        if (key && (held.has(key) || buttonKeys.has(key))) stop(e);
    };
    const up = e => {
        const key = activation(e);
        if (key && held.delete(key)) { stop(e); return; }
        const button = key && buttonKeys.get(key);
        if (button) {
            buttonKeys.delete(key);
            e.stopPropagation();
            // Space's default click belongs only to the original button. A
            // command which moves focus must not click its newly focused target.
            if (e.target !== button) e.preventDefault();
        }
    };
    const clear = () => { held.clear(); buttonKeys.clear(); };
    root.addEventListener('keydown', down);
    host.addEventListener('keydown', repeat, true);
    host.addEventListener('keyup', up, true);
    host.addEventListener('blur', clear);
    host.addEventListener('pagehide', clear);
    const unbind = () => {
        clear(); root.removeEventListener('keydown', down);
        host.removeEventListener('keydown', repeat, true);
        host.removeEventListener('keyup', up, true);
        host.removeEventListener('blur', clear); host.removeEventListener('pagehide', clear);
    };
    // Keep bind's callable disposal contract, exposing only a bounded boolean
    // view of gestures which are intentionally absent from native key routing.
    unbind.isActivationHeld = () => held.size !== 0 || buttonKeys.size !== 0;
    return unbind;
}
export function isActivationHeld() { return dispose?.isActivationHeld() ?? false; }
export function install() {
    const root = document.getElementById('uno-semantics-root');
    if (!root) throw new Error('The Uno accessibility event root is not ready.');
    if (installedRoot === root) return;
    dispose?.(); dispose = bind(root, globalThis); installedRoot = root;
}
""";
#endif
}
