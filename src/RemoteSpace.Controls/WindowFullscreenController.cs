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
    private Task? _initialization, _disposal;
    private readonly string _id = Guid.NewGuid().ToString("N");
    public bool IsFullScreen { get; private set; }
    public event Action<bool>? Changed;
    public WindowFullscreenController(FrameworkElement owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }
    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();
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
        if (active && !view.IsFullScreenMode && !view.TryEnterFullScreenMode())
            throw new InvalidOperationException("Full screen was declined by the host.");
        if (!active && view.IsFullScreenMode) view.ExitFullScreenMode();
        Publish(view.IsFullScreenMode);
        return Task.CompletedTask;
#endif
    }
    private void VerifyAccess()
    {
        if (!_owner.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("Full screen requires the owner's UI thread.");
    }
    private void Publish(bool value)
    {
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
        if (_ready && IsFullScreen) ApplicationView.GetForCurrentView().ExitFullScreenMode();
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
    [JSImport("unregister", "remotespace.fullscreen")]
    [return: JSMarshalAs<JSType.Promise<JSType.Boolean>>]
    private static partial Task<bool> Unregister(string id);
    private const string Source = """
export function createController(doc, changed) {
    let disposed = false, busy = false, owned = false, previous = false, operation, disposal;
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
    doc.addEventListener('fullscreenchange', publish);
    return {
        set(value) {
            if (disposed) return Promise.reject(new Error('Fullscreen controller is disposed.'));
            if (typeof value !== 'boolean') return Promise.reject(new Error('Invalid fullscreen state.'));
            if (busy) return Promise.reject(new Error('A fullscreen change is already pending.'));
            if (active() === value) { publish(); return Promise.resolve(value); }
            if (value && (doc.fullscreenElement || !doc.fullscreenEnabled || !doc.documentElement.requestFullscreen))
                return Promise.reject(new Error('Full screen is unavailable for this document.'));
            busy = true;
            try {
                // Invoke now, on the original activation stack, not after an await.
                const pending = value ? doc.documentElement.requestFullscreen({navigationUI:'hide'}) : doc.exitFullscreen();
                if (value) owned = true;
                operation = Promise.resolve(pending).then(async () => {
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
export async function unregister(id) {
    const owner = controllers.get(id);
    if (!owner) return false;
    // Retain the registration until pending platform work drains. A new owner
    // must not acquire the document while the previous owner is still exiting.
    try { return await owner.dispose(); }
    finally { if (controllers.get(id) === owner) controllers.delete(id); }
}
""";
#endif
}
