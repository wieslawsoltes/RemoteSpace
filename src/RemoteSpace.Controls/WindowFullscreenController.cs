using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;
#if __WASM__
using System.Runtime.InteropServices.JavaScript;
using System.Text;
#endif

namespace RemoteSpace.Controls;

/// <summary>UI-owner fullscreen lifecycle, independent of RDP or app layout. The host
/// initializes before user activation; requests report actual platform completion.
/// Browser Escape/host exits are observed without polling or keyboard locking.</summary>
public sealed partial class WindowFullscreenController : IAsyncDisposable
{
    private readonly FrameworkElement _owner;
    private bool _ready, _disposed;
#if !__WASM__
    private bool _ownsNativeFullscreen;
#endif
    private Task? _initialization, _disposal;
    private readonly string _id = Guid.NewGuid().ToString("N");
    public bool IsFullScreen { get; private set; }
    public event Action<bool>? Changed;
    public WindowFullscreenController(FrameworkElement owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }
    public Task InitializeAsync()
    {
        VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
        return _initialization ??= InitializeCoreAsync();
    }
    private async Task InitializeCoreAsync()
    {
        VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
#if __WASM__
        await Module;
        ObjectDisposedException.ThrowIf(_disposed, this);
        Register(_id, Publish);
#else
        _owner.SizeChanged += SizeChanged;
        Publish(ApplicationView.GetForCurrentView().IsFullScreenMode);
#endif
        _ready = true;
    }
    /// <summary>Call directly from the user command. No module loading, timer or
    /// asynchronous focus operation is inserted before the permission-sensitive call.</summary>
    public Task SetAsync(bool active)
    {
        VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ready) throw new InvalidOperationException("Full screen is not initialized. Reload the app and try again.");
#if __WASM__
        return SetBrowser(_id, active);
#else
        var view = ApplicationView.GetForCurrentView();
        if (active && !view.IsFullScreenMode)
        {
            if (!view.TryEnterFullScreenMode()) throw new InvalidOperationException("Full screen was declined by the host.");
            _ownsNativeFullscreen = true;
        }
        if (!active && view.IsFullScreenMode) view.ExitFullScreenMode();
        Publish(view.IsFullScreenMode);
        return Task.CompletedTask;
#endif
    }
    /// <summary>Terminal exit for session close/end. Unlike a new user toggle,
    /// this shares one pending exit and drains any already admitted browser
    /// request first. A failed entry does not prevent teardown; an exit failure
    /// is still reported. Unowned host fullscreen is preserved; an explicit
    /// user exit can instead call SetAsync(false). No new entry is admitted
    /// while the terminal operation drains.</summary>
    public Task ExitAsync()
    {
        VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ready) throw new InvalidOperationException("Full screen is not initialized.");
#if __WASM__
        return ExitBrowser(_id);
#else
        if (!_ownsNativeFullscreen)
        {
            Publish(ApplicationView.GetForCurrentView().IsFullScreenMode);
            return Task.CompletedTask;
        }
        return SetAsync(false);
#endif
    }
    private void VerifyAccess()
    {
        if (!_owner.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("Full screen requires the owner's UI thread.");
    }
    private void Publish(bool value)
    {
#if !__WASM__
        if (!value) _ownsNativeFullscreen = false;
#endif
        if (_disposed || value == IsFullScreen) return;
        IsFullScreen = value; Changed?.Invoke(value);
    }
#if !__WASM__
    private void SizeChanged(object sender, SizeChangedEventArgs e) => Publish(ApplicationView.GetForCurrentView().IsFullScreenMode);
#endif
    public ValueTask DisposeAsync()
    {
        VerifyAccess();
        return new(_disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        _disposed = true; Changed = null;
#if __WASM__
        if (_ready) await Unregister(_id);
#else
        _owner.SizeChanged -= SizeChanged;
        if (_ready && _ownsNativeFullscreen && ApplicationView.GetForCurrentView().IsFullScreenMode)
            ApplicationView.GetForCurrentView().ExitFullScreenMode();
        _ownsNativeFullscreen = false;
        await Task.CompletedTask;
#endif
        _ready = false; IsFullScreen = false;
    }
#if __WASM__
    private static readonly Task<JSObject> Module = JSHost.ImportAsync("remotespace.fullscreen",
        "data:text/javascript;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(Source)));
    [JSImport("register", "remotespace.fullscreen")]
    private static partial void Register(string id, [JSMarshalAs<JSType.Function<JSType.Boolean>>] Action<bool> changed);
    [JSImport("set", "remotespace.fullscreen")]
    [return: JSMarshalAs<JSType.Promise<JSType.Boolean>>]
    private static partial Task<bool> SetBrowser(string id, bool active);
    [JSImport("exit", "remotespace.fullscreen")]
    [return: JSMarshalAs<JSType.Promise<JSType.Boolean>>]
    private static partial Task<bool> ExitBrowser(string id);
    [JSImport("unregister", "remotespace.fullscreen")]
    [return: JSMarshalAs<JSType.Promise<JSType.Boolean>>]
    private static partial Task<bool> Unregister(string id);
    private const string Source = """
export function createController(doc, changed) {
    let disposed = false, busy = false, owned = false, previous = false, operation, disposal;
    let closing = false, terminalExit;
    const active = () => doc.fullscreenElement === doc.documentElement;
    const publish = () => {
        const value = active();
        if (!disposed && previous !== value) { previous = value; changed(value); }
        if (!value && !busy) owned = false;
        return value;
    };
    const leaveOwned = async () => {
        if (owned && active()) await doc.exitFullscreen();
        owned = false;
        return false;
    };
    const change = (value, terminal = false) => {
        if (disposed) return Promise.reject(new Error('Fullscreen controller is disposed.'));
        if (typeof value !== 'boolean') return Promise.reject(new Error('Invalid fullscreen state.'));
        if (busy || (closing && !terminal)) return Promise.reject(new Error('A fullscreen change is already pending.'));
        if (active() === value) { publish(); return Promise.resolve(value); }
        if (value && (doc.fullscreenElement || !doc.fullscreenEnabled || !doc.documentElement.requestFullscreen))
            return Promise.reject(new Error('Full screen is unavailable for this document.'));
        busy = true;
        try {
            // Invoke now, on the original activation stack, not after an await.
            const pending = value ? doc.documentElement.requestFullscreen({navigationUI:'hide'}) : doc.exitFullscreen();
            operation = Promise.resolve(pending).then(async () => {
                // A denied request cannot acquire someone else's fullscreen.
                if (value) owned = active();
                if (disposed) return leaveOwned();
                const result = publish();
                if (result !== value) throw new Error('The host did not apply the fullscreen request.');
                return result;
            }).catch(error => {
                if (!disposed) publish();
                throw error;
            }).finally(() => { busy = false; if (!active()) owned = false; });
            return operation;
        } catch (error) { busy = false; publish(); return Promise.reject(error); }
    };
    doc.addEventListener('fullscreenchange', publish);
    return {
        set: value => change(value),
        exit() {
            if (disposed) return Promise.reject(new Error('Fullscreen controller is disposed.'));
            if (terminalExit) return terminalExit;
            // Reserve one terminal operation immediately. Exiting needs no new
            // user activation, so it may drain entry without queuing new commands.
            closing = true;
            terminalExit = Promise.resolve().then(async () => {
                if (operation) { try { await operation; } catch { /* The initiating caller observes its error. */ } }
                if (disposed) return false;
                // Automatic teardown cannot leave another host's fullscreen,
                // including one acquired while our pending entry was denied.
                // Return the real state rather than claiming an exit occurred.
                if (!owned) return publish();
                return change(false, true);
            }).finally(() => { closing = false; terminalExit = undefined; });
            return terminalExit;
        },
        dispose() {
            if (disposal) return disposal;
            disposed = true; doc.removeEventListener('fullscreenchange', publish);
            disposal = (async () => {
                if (operation) { try { await operation; } catch { /* Owner observes request failure. */ } }
                return leaveOwned();
            })();
            return disposal;
        }
    };
}
const controllers = new Map();
export function register(id, changed) {
    if (!id || typeof changed !== 'function') throw new Error('Invalid fullscreen registration.');
    if (controllers.size) throw new Error('The document already has a fullscreen owner.');
    controllers.set(id, createController(document, changed));
}
export function set(id, value) {
    const owner = controllers.get(id);
    if (!owner) return Promise.reject(new Error('Full screen is not initialized.'));
    return owner.set(value);
}
export function exit(id) {
    const owner = controllers.get(id);
    if (!owner) return Promise.reject(new Error('Full screen is not initialized.'));
    return owner.exit();
}
export async function unregister(id) {
    const owner = controllers.get(id);
    if (!owner) return false;
    // Retain registration while platform cleanup drains; a new owner cannot
    // acquire the document while the previous owner is still exiting.
    try { return await owner.dispose(); }
    finally { if (controllers.get(id) === owner) controllers.delete(id); }
}
""";
#endif
}
