using Microsoft.UI.Xaml;
using RemoteSpace.Core;

namespace RemoteSpace.Controls;

/// <summary>Reusable UI-thread display coordinator. Uses the full view's layout
/// size (not its zoom/scrollbar-reduced canvas) to avoid resize feedback loops.
/// The host owns the session and disposes this coordinator before the view.</summary>
public sealed class SessionDisplayController : IAsyncDisposable
{
    private readonly IRemoteSession _session;
    private readonly IRemoteDisplayControl _display;
    private readonly RemoteDesktopView _view;
    private readonly DisplayResizeQueue _queue = new();
    private readonly DispatcherTimer _timer = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private Task _automaticOperation = Task.CompletedTask;
    private Task? _disposal;
    private bool _disposed;
    private int _manualRequests;
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(10);
    public event Action? Changed;
    public event Action<string>? Failed;
    public long AutomaticRequests => _queue.StartedRequests;
    public bool AutomaticResolution
    {
        get => _queue.Enabled;
        set
        {
            VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
            if (_queue.Enabled == value) return;
            _queue.SetEnabled(value, Environment.TickCount64);
            if (value) _view.SetScale(null);
            Refresh(); Changed?.Invoke();
        }
    }

    public SessionDisplayController(IRemoteSession session, RemoteDesktopView view)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(view);
        _session = session; _view = view;
        _display = session as IRemoteDisplayControl ?? throw new ArgumentException("The session has no display-control extension.", nameof(session));
        VerifyAccess();
        _timer.Tick += Tick;
        view.SizeChanged += SizeChanged; view.Loaded += VisibilityChanged; view.Unloaded += VisibilityChanged;
        view.PresentationActivityChanged += Refresh;
        session.StateChanged += SessionStateChanged;
        session.DesktopResized += DesktopResized;
        _display.DisplayCapabilitiesChanged += CapabilitiesChanged;
        if (session is IRemoteGraphicsOutput output) output.OutputEnabledChanged += OutputChanged;
        Refresh();
    }

    private void VerifyAccess()
    {
        if (!_view.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("Display coordination requires the view's UI thread.");
    }
    private void QueueRefresh() => _view.DispatcherQueue.TryEnqueue(() => { if (!_disposed) Refresh(); });
    private void SessionStateChanged(SessionState state) => QueueRefresh();
    private void DesktopResized(DesktopSize size) => QueueRefresh();
    private void CapabilitiesChanged(DisplayCapabilities? caps) => QueueRefresh();
    private void OutputChanged(bool enabled) => QueueRefresh();
    private void SizeChanged(object sender, SizeChangedEventArgs e) => Refresh();
    private void VisibilityChanged(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (_disposed) return;
        _timer.Stop();
        long now = Environment.TickCount64;
        bool available = _manualRequests == 0 && _view.IsLoaded && _view.IsPresentationActive &&
            _session.State == SessionState.Active && _display.DisplayCapabilities is not null &&
            (_session is not IRemoteGraphicsOutput output || output.OutputEnabled);
        _queue.SetAvailable(available, now);
        _queue.Offer(_view.ActualWidth, _view.ActualHeight, _display.DisplayCapabilities, now);
        if (_queue.DelayMilliseconds(now, _view.DesktopWidth, _view.DesktopHeight) is { } delay)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(delay);
            _timer.Start();
        }
    }

    private void Tick(object? sender, object e)
    {
        _timer.Stop();
        Refresh(); // Recheck visibility, capabilities and actual dimensions at send time.
        if (!_disposed && _queue.TryBegin(Environment.TickCount64, _view.DesktopWidth, _view.DesktopHeight, out var request))
        {
            _timer.Stop();
            _automaticOperation = ApplyAutomaticAsync(request);
        }
    }
    private async Task ApplyAutomaticAsync(DisplayResizeRequest request)
    {
        bool confirmed = false;
        string? failure = null;
        try
        {
            await ApplySerializedAsync(new(0, 0, request.Target.Width, request.Target.Height), _stop.Token);
            confirmed = true;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception e) { failure = "Automatic resolution stopped: " + e.Message; }
        finally
        {
            _queue.Complete(request.Ticket, confirmed, Environment.TickCount64);
            if (!_disposed)
            {
                Refresh(); Changed?.Invoke();
                if (failure is not null) Failed?.Invoke(failure);
            }
        }
    }

    /// <summary>A manual resolution supersedes automatic mode, waits behind any
    /// in-flight confirmation, and shares the same writer and lifecycle token.</summary>
    public async Task RequestResolutionAsync(DisplayMonitor monitor, CancellationToken cancellationToken = default)
    {
        VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(monitor);
        DisplayLayout.Validate([monitor], _display.DisplayCapabilities ?? throw new InvalidOperationException("Remote resolution is not available."));
        AutomaticResolution = false;
        _manualRequests++;
        Refresh();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        try
        {
            await ApplySerializedAsync(monitor, stop.Token);
            if (!_disposed) _view.SetScale(null);
        }
        finally { _manualRequests--; Refresh(); }
    }
    private async Task ApplySerializedAsync(DisplayMonitor monitor, CancellationToken ct)
    {
        await _writer.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_view.DesktopWidth == monitor.Width && _view.DesktopHeight == monitor.Height) return;
            await _view.ReleaseHeldInputAsync(ct);
            ct.ThrowIfCancellationRequested();
            await RemoteDisplayRequest.ApplyAsync(_session, _display, monitor, ConfirmationTimeout, ct);
        }
        finally { _writer.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        VerifyAccess();
        return new(_disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        _disposed = true; _timer.Stop(); _stop.Cancel();
        _timer.Tick -= Tick;
        _view.SizeChanged -= SizeChanged; _view.Loaded -= VisibilityChanged; _view.Unloaded -= VisibilityChanged;
        _view.PresentationActivityChanged -= Refresh;
        _session.StateChanged -= SessionStateChanged; _session.DesktopResized -= DesktopResized;
        _display.DisplayCapabilitiesChanged -= CapabilitiesChanged;
        if (_session is IRemoteGraphicsOutput output) output.OutputEnabledChanged -= OutputChanged;
        await _automaticOperation;
        await _writer.WaitAsync(); // Drain an already executing manual request before disposal.
        _writer.Release(); _writer.Dispose(); _stop.Dispose();
    }
}
