using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RemoteSpace.Core;
using Windows.System;
using CoreVirtualKeyStates = Windows.UI.Core.CoreVirtualKeyStates;
#if __WASM__
using System.Runtime.InteropServices.JavaScript;
using System.Text;
#endif

namespace RemoteSpace.Controls;

/// <summary>Opt-in local session shortcuts for an owned remote input surface.
/// The caller supplies UI actions and owns transport/input release. Only Home
/// and Pause gestures are consumed; browser policy and other shortcuts are not
/// replaced. Dispose on the UI thread before releasing the session view.</summary>
public sealed partial class SessionControlKeys : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _root;
    private readonly Control _scope;
    private readonly Func<SessionControlShortcut, CancellationToken, Task> _execute;
    private readonly Action<string> _report;
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed, _busy;
#if __WASM__
    private readonly string _id = "remotespace-session-keys-" + Guid.NewGuid().ToString("N");
    private string? _previousId;
    private bool _registered;
#else
    private readonly SessionControlShortcutState _state = new();
#endif

    private SessionControlKeys(Window window, FrameworkElement root, Control scope,
        Func<SessionControlShortcut, CancellationToken, Task> execute, Action<string> report)
    {
        _window = window; _root = root; _scope = scope; _execute = execute; _report = report;
    }

    public static async Task<SessionControlKeys> AttachAsync(Window window, FrameworkElement root, Control scope,
        Func<SessionControlShortcut, CancellationToken, Task> execute, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(window); ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(execute); ArgumentNullException.ThrowIfNull(report);
        if (!root.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("Session keys require the owner's UI thread.");
        var owner = new SessionControlKeys(window, root, scope, execute, report);
        try
        {
#if __WASM__
            await Module;
            owner._previousId = AutomationProperties.GetAutomationId(scope);
            AutomationProperties.SetAutomationId(scope, owner._id);
            Register(owner._id, owner.Dispatch);
            owner._registered = true;
            scope.GotFocus += owner.FocusChanged;
            scope.LostFocus += owner.FocusChanged;
            scope.Loaded += owner.FocusChanged;
            scope.Unloaded += owner.ScopeUnloaded;
            owner.PublishNativeFocus();
#else
            root.PreviewKeyDown += owner.KeyDown;
            root.PreviewKeyUp += owner.KeyUp;
            window.Activated += owner.Activated;
            await Task.CompletedTask;
#endif
            return owner;
        }
        catch { owner.Dispose(); throw; }
    }

    // Invocation remains on the incoming activation stack until the host's own
    // first await, allowing its ordinary requestFullscreen call to use that
    // user activation. New commands while one runs are not queued.
    private async void Dispatch(int value, bool requiresNativeFocus)
    {
        if (_disposed || _busy || !_scope.IsLoaded || !_scope.IsEnabled) return;
        if (value is not (1 or 2)) return;
        if (requiresNativeFocus && (_scope.XamlRoot is null ||
            !ReferenceEquals(FocusManager.GetFocusedElement(_scope.XamlRoot), _scope))) return;
        _busy = true;
        try { await _execute((SessionControlShortcut)value, _stop.Token); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed) _report("Session shortcut: " + error.Message); }
        finally { _busy = false; }
    }
#if __WASM__
    private void FocusChanged(object sender, RoutedEventArgs args) => PublishNativeFocus();
    private void ScopeUnloaded(object sender, RoutedEventArgs args)
    {
        if (_registered && !_disposed) SetNativeFocus(_id, false);
    }
    private void PublishNativeFocus()
    {
        if (!_registered || _disposed) return;
        SetNativeFocus(_id, _scope.IsLoaded && _scope.XamlRoot is not null &&
            ReferenceEquals(FocusManager.GetFocusedElement(_scope.XamlRoot), _scope));
    }
#else
    private static bool Down(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
    private void KeyDown(object sender, KeyRoutedEventArgs args)
    {
        // Ordinary typing takes no ancestor walk or native modifier queries.
        if (_disposed || args.Key is not (VirtualKey.Home or VirtualKey.Pause)) return;
        if (_state.KeyDown((uint)args.Key, false, false, false, false, false, true, out _))
        { args.Handled = true; return; }
        if (args.Handled || !_scope.IsLoaded || !_scope.IsEnabled) return;
        // Ordinary typing, dialogs, menus and workspace fields are not a session
        // shortcut scope. Key releases below may follow a local focus transfer.
        bool within = false;
        for (var node = args.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is TextBox or PasswordBox) return;
            if (ReferenceEquals(node, _scope)) { within = true; break; }
        }
        if (!within) return;
        if (_state.KeyDown((uint)args.Key, Down(VirtualKey.Control), Down(VirtualKey.Menu), Down(VirtualKey.Shift),
            Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows), Down(VirtualKey.RightMenu), args.KeyStatus.WasKeyDown, out var command))
        {
            args.Handled = true;
            if (command != SessionControlShortcut.None) Dispatch((int)command, false);
        }
    }
    private void KeyUp(object sender, KeyRoutedEventArgs args) { if (_state.KeyUp((uint)args.Key)) args.Handled = true; }
    private void Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == Windows.UI.Core.CoreWindowActivationState.Deactivated) _state.Reset();
    }
#endif
    public void Dispose()
    {
        if (!_root.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("Session keys require the owner's UI thread.");
        if (_disposed) return;
        _disposed = true;
        try { _stop.Cancel(); }
        finally
        {
            // A host cancellation callback can throw. The local input owner
            // must still detach, and cancellation remains observable to caller.
            try
            {
#if __WASM__
                _scope.GotFocus -= FocusChanged; _scope.LostFocus -= FocusChanged;
                _scope.Loaded -= FocusChanged; _scope.Unloaded -= ScopeUnloaded;
                try { if (_registered) Unregister(_id); }
                finally
                {
                    _registered = false;
                    if (_previousId is not null && AutomationProperties.GetAutomationId(_scope) == _id)
                        AutomationProperties.SetAutomationId(_scope, _previousId);
                }
#else
                _root.PreviewKeyDown -= KeyDown; _root.PreviewKeyUp -= KeyUp;
                _window.Activated -= Activated; _state.Reset();
#endif
            }
            finally { _stop.Dispose(); }
        }
    }
#if __WASM__
    private static readonly Task<JSObject> Module = JSHost.ImportAsync("remotespace.session-keys",
        "data:text/javascript;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(Source)));
    [JSImport("register", "remotespace.session-keys")]
    private static partial void Register(string id, [JSMarshalAs<JSType.Function<JSType.Number, JSType.Boolean>>] Action<int, bool> execute);
    [JSImport("unregister", "remotespace.session-keys")]
    private static partial void Unregister(string id);
    [JSImport("setNativeFocus", "remotespace.session-keys")]
    private static partial void SetNativeFocus(string id, bool focused);
    private const string Source = """
export function bind(root, host, registrations, getNativeFocus = () => undefined, doc = root?.ownerDocument) {
    // Two fixed physical command keys; no arbitrary input or event objects.
    const held = new Map();
    const key = event => event.code === 'Home' ? 1 : event.code === 'Pause' ? 2 : 0;
    const stop = event => { event.preventDefault(); event.stopPropagation(); };
    const down = event => {
        const command = key(event);
        if (!command) return;
        if (held.has(command)) { stop(event); return; }
        if (event.defaultPrevented || event.repeat || event.isComposing || !event.ctrlKey || !event.altKey ||
            event.shiftKey || event.metaKey || event.getModifierState?.('AltGraph')) return;
        const path = event.composedPath();
        if (path.some(node => node?.tagName === 'INPUT' || node?.tagName === 'TEXTAREA' ||
            node?.isContentEditable || node?.getAttribute?.('role') === 'dialog')) return;
        const semanticRoot = typeof root === 'function' ? root() : root;
        if (semanticRoot && path.includes(semanticRoot)) {
            for (const node of path) {
                const id = node?.getAttribute?.('xamlautomationid');
                const execute = id && registrations.get(id);
                if (!execute) continue;
                held.set(command, id);
                stop(event);
                execute(command, false);
                return;
            }
            return; // A local semantic command cannot borrow stale desktop focus.
        }
        // Without accessibility peers, Uno receives keys at its primary canvas
        // or document root. Only native focus published by an attached scope
        // grants fallback ownership. Arbitrary DOM/canvas targets are excluded.
        const target = path[0];
        if (!target || !doc || (target !== doc.body && target !== doc.documentElement &&
            target !== doc.getElementById('uno-canvas'))) return;
        const id = getNativeFocus(), execute = id && registrations.get(id);
        if (!execute) return;
        held.set(command, id);
        stop(event);
        execute(command, true);
    };
    const up = event => { if (held.delete(key(event))) stop(event); };
    const clear = () => held.clear();
    host.addEventListener('keydown', down, true);
    host.addEventListener('keyup', up, true);
    host.addEventListener('blur', clear);
    host.addEventListener('pagehide', clear);
    return {
        forget(id) { for (const [command, owner] of held) if (owner === id) held.delete(command); },
        dispose() {
            clear(); host.removeEventListener('keydown', down, true); host.removeEventListener('keyup', up, true);
            host.removeEventListener('blur', clear); host.removeEventListener('pagehide', clear);
        }
    };
}
const registrations = new Map();
let router, nativeFocus;
export function register(id, execute) {
    if (!id || typeof execute !== 'function') throw new Error('Invalid session shortcut registration.');
    if (registrations.has(id)) throw new Error('Session shortcuts already registered.');
    if (registrations.size >= 32) throw new Error('Session shortcut registration limit exceeded.');
    if (!document.getElementById('uno-semantics-root') && !document.getElementById('uno-canvas'))
        throw new Error('The Uno input host is not ready.');
    // Resolve the semantic root only for a candidate chord: accessibility can
    // be enabled after the session starts, without replacing this registration.
    router ??= bind(() => document.getElementById('uno-semantics-root'), globalThis,
        registrations, () => nativeFocus, document);
    registrations.set(id, execute);
}
export function setNativeFocus(id, focused) {
    if (focused && registrations.has(id)) nativeFocus = id;
    else if (nativeFocus === id) nativeFocus = undefined;
}
export function unregister(id) {
    registrations.delete(id); router?.forget(id);
    if (nativeFocus === id) nativeFocus = undefined;
    if (!registrations.size) { router?.dispose(); router = nativeFocus = undefined; }
}
""";
#endif
}
