using Microsoft.UI.Xaml;
using RemoteSpace.Core;

namespace RemoteSpace.Controls;

/// <summary>UI-thread display coordinator. Keeps one automatic and at most one
/// waiting manual request. It observes the whole view, not the scrollbar-reduced
/// canvas, and drains both operation owners before disposing their lifetime.</summary>
public sealed class SessionDisplayController : IAsyncDisposable
{
    private readonly IRemoteSession _session;
    private readonly IRemoteDisplayControl _display;
    private readonly RemoteDesktopView _view;
    private readonly DisplayResizeQueue _queue = new();
    private readonly DispatcherTimer _timer = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _automaticOperation = Task.CompletedTask, _manualOperation = Task.CompletedTask;
    private Task? _disposal;
    private bool _disposed;
    private int _refreshQueued;
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
        session.StateChanged += SessionStateChanged; session.DesktopResized += DesktopResized;
        _display.DisplayCapabilitiesChanged += CapabilitiesChanged;
        if (session is IRemoteGraphicsOutput output) output.OutputEnabledChanged += OutputChanged;
        Refresh();
    }
    private void VerifyAccess()
    {
        if (!_view.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("Display coordination requires the view's UI thread.");
    }
    private void QueueRefresh()
    {
        // Server notifications can arrive on a worker. Retain at most one UI
        // callback; that callback reads current state rather than replaying it.
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        if (!_view.DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (!_disposed) Refresh();
        })) Interlocked.Exchange(ref _refreshQueued, 0);
    }
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
        bool available = _manualOperation.IsCompleted && _view.IsLoaded && _view.IsPresentationActive &&
            _session.State == SessionState.Active && _display.DisplayCapabilities is not null &&
            (_session is not IRemoteGraphicsOutput output || output.OutputEnabled);
        _queue.SetAvailable(available, now);
        _queue.Offer(_view.ActualWidth, _view.ActualHeight, _display.DisplayCapabilities, now);
        if (_queue.DelayMilliseconds(now, _view.DesktopWidth, _view.DesktopHeight) is { } delay)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(delay); _timer.Start();
        }
    }
    private void Tick(object? sender, object e)
    {
        _timer.Stop(); Refresh();
        if (!_disposed && _queue.TryBegin(Environment.TickCount64, _view.DesktopWidth, _view.DesktopHeight, out var request))
        {
            _timer.Stop();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _automaticOperation = completion.Task; // Publish before any reentrant callback.
            _ = ApplyAutomaticAsync(request, completion);
        }
    }
    private async Task ApplyAutomaticAsync(DisplayResizeRequest request, TaskCompletionSource completion)
    {
        bool confirmed = false; string? failure = null;
        try
        {
            await ApplyAsync(new(0, 0, request.Target.Width, request.Target.Height), _stop.Token);
            confirmed = true;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { failure = "Automatic resolution stopped: " + error.Message; }
        finally
        {
            _queue.Complete(request.Ticket, confirmed, Environment.TickCount64);
            // Publish completion before scheduling the next latest target.
            completion.TrySetResult();
            if (!_disposed)
            {
                Refresh(); Changed?.Invoke();
                if (failure is not null) Failed?.Invoke(failure);
            }
        }
    }

    /// <summary>Reserve one manual command before changing automatic mode. A
    /// pre-cancelled/invalid/duplicate request has no presentation side effects.
    /// The accepted command drains an already-started automatic request first.</summary>
    public Task RequestResolutionAsync(DisplayMonitor monitor, CancellationToken cancellationToken = default)
    {
        VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(monitor); cancellationToken.ThrowIfCancellationRequested();
        DisplayLayout.Validate([monitor], _display.DisplayCapabilities ?? throw new InvalidOperationException("Remote resolution is not available."));
        if (!_manualOperation.IsCompleted) throw new InvalidOperationException("A manual resolution request is already pending.");
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manualOperation = completion.Task;
        _ = ApplyManualAsync(monitor, stop, _automaticOperation, completion);
        return completion.Task;
    }
    private async Task ApplyManualAsync(DisplayMonitor monitor, CancellationTokenSource stop, Task automatic, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            _queue.SetEnabled(false, Environment.TickCount64);
            Refresh(); Changed?.Invoke();
            await automatic.WaitAsync(stop.Token);
            await ApplyAsync(monitor, stop.Token);
            if (!_disposed) _view.SetScale(null);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            stop.Dispose();
            if (failure is OperationCanceledException cancelled) completion.TrySetCanceled(cancelled.CancellationToken);
            else if (failure is not null) completion.TrySetException(failure);
            else completion.TrySetResult();
            if (!_disposed) Refresh();
        }
    }
    private async Task ApplyAsync(DisplayMonitor monitor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ConfirmationTimeout);
        try
        {
            await SessionReadiness.WaitForActiveAsync(_session, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            DisplayLayout.Validate([monitor], _display.DisplayCapabilities ?? throw new InvalidOperationException("Remote resolution is no longer available."));
            if (_view.DesktopWidth == monitor.Width && _view.DesktopHeight == monitor.Height) return;
            await _view.ReleaseHeldInputAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            await RemoteDisplayRequest.ApplyAsync(_session, _display, monitor, ConfirmationTimeout, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The remote resolution request did not complete before its deadline.");
        }
    }

    public ValueTask DisposeAsync()
    {
        VerifyAccess();
        if (_disposal is not null) return new(_disposal);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _disposal = completion.Task; _disposed = true;
        _ = DisposeCoreAsync(completion);
        return new(completion.Task);
    }
    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            _timer.Stop(); _timer.Tick -= Tick;
            _view.SizeChanged -= SizeChanged; _view.Loaded -= VisibilityChanged; _view.Unloaded -= VisibilityChanged;
            _view.PresentationActivityChanged -= Refresh;
            _session.StateChanged -= SessionStateChanged; _session.DesktopResized -= DesktopResized;
            _display.DisplayCapabilitiesChanged -= CapabilitiesChanged;
            if (_session is IRemoteGraphicsOutput output) output.OutputEnabledChanged -= OutputChanged;
            Changed = null; Failed = null;
            await AsyncCleanupSequence.RunAsync(
                () => { _stop.Cancel(); return ValueTask.CompletedTask; },
                async () =>
                {
                    // Initiators observe their request failures. Teardown owns
                    // completion, not a second delivery of cancellation/errors.
                    try { await Task.WhenAll(_automaticOperation, _manualOperation); }
                    catch { }
                });
        }
        catch (Exception error) { failure = error; }
        finally { _stop.Dispose(); }
        if (failure is not null) completion.TrySetException(failure); else completion.TrySetResult();
    }
}
