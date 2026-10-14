using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RemoteSpace.Controls;

/// <summary>Transfers local focus once a reattached control has joined the live
/// layout. Uses public native focus only: it neither focuses a DOM proxy nor
/// changes keyboard routing. No listeners or timers remain after completion.</summary>
public static class ControlFocus
{
    public static async Task WhenReadyAsync(Control target, FrameworkElement owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.DispatcherQueue.HasThreadAccess)
            throw new InvalidOperationException("Focus transfer requires the owner's UI thread.");
        if (!owner.IsLoaded) throw new OperationCanceledException("The focus owner is unloaded.");

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // This is a deadline, not a focus retry/polling loop. A control which
        // never becomes focusable must not retain the initiating command forever.
        var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        bool queued = false, finished = false;
        bool Ready() => target.IsLoaded && target.XamlRoot is not null &&
            target.Visibility == Visibility.Visible && target.IsEnabled &&
            target.ActualWidth > 0 && target.ActualHeight > 0;
        void Cancel() => completion.TrySetCanceled(new CancellationToken(true));
        void Check()
        {
            if (finished || queued || completion.Task.IsCompleted || !Ready()) return;
            queued = true;
            // Loaded handlers may still be installing nested command panels.
            // Complete that native lifecycle before one focus attempt.
            if (!owner.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                queued = false;
                if (finished || completion.Task.IsCompleted) return;
                if (cancellationToken.IsCancellationRequested || !owner.IsLoaded) { Cancel(); return; }
                if (!Ready()) return; // The next real load/layout event owns readiness.
                try
                {
                    if (!target.Focus(FocusState.Programmatic))
                        throw new InvalidOperationException("The restored local command could not receive keyboard focus.");
                    completion.TrySetResult();
                }
                catch (Exception error) { completion.TrySetException(error); }
            })) completion.TrySetException(new InvalidOperationException("The focus dispatcher is unavailable."));
        }
        void Loaded(object sender, RoutedEventArgs args) => Check();
        void Layout(object? sender, object args) => Check();
        void Unloaded(object sender, RoutedEventArgs args) => Cancel();
        void Timeout(object? sender, object args) => completion.TrySetException(new TimeoutException("The local command did not join the visible layout."));

        target.Loaded += Loaded;
        target.LayoutUpdated += Layout;
        owner.Unloaded += Unloaded;
        deadline.Tick += Timeout;
        using var registration = cancellationToken.Register(() =>
        {
            if (owner.DispatcherQueue.HasThreadAccess) Cancel();
            else if (!owner.DispatcherQueue.TryEnqueue(Cancel)) Cancel();
        });
        try
        {
            deadline.Start();
            Check();
            await completion.Task;
        }
        finally
        {
            finished = true;
            deadline.Stop(); deadline.Tick -= Timeout;
            target.Loaded -= Loaded; target.LayoutUpdated -= Layout;
            owner.Unloaded -= Unloaded;
        }
    }
}
