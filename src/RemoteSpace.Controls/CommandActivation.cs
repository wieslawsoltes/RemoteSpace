using System.Diagnostics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Windows.System;
using Windows.UI.Core;

namespace RemoteSpace.Controls;

/// <summary>Separates a popup-opening command from the input gesture that invoked it.
/// Call on the UI thread before creating a dialog with a default command. The first
/// timer turn lets both semantic and native routing complete, then held activation
/// keys must be released. No timer remains when complete, cancelled or unloaded.</summary>
public static class CommandActivation
{
    public static Task WaitForReleaseAsync(FrameworkElement owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.DispatcherQueue.HasThreadAccess)
            throw new InvalidOperationException("Command activation must be awaited from the UI thread.");
        if (!owner.IsLoaded) return Task.FromCanceled(new CancellationToken(true));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        long started = Stopwatch.GetTimestamp();
        CancellationTokenRegistration registration = default;
        bool finished = false;
        void Finish(Exception? error = null)
        {
            if (finished) return;
            finished = true; timer.Stop(); timer.Tick -= Tick; owner.Unloaded -= Unloaded;
            registration.Unregister();
            if (error is OperationCanceledException) completion.TrySetCanceled();
            else if (error is not null) completion.TrySetException(error);
            else completion.TrySetResult();
        }
        void Unloaded(object sender, RoutedEventArgs args) => Finish(new OperationCanceledException());
        void Tick(object? sender, object args)
        {
            if (cancellationToken.IsCancellationRequested) { Finish(new OperationCanceledException()); return; }
            bool pressed = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Enter) & CoreVirtualKeyStates.Down) != 0 ||
                (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Space) & CoreVirtualKeyStates.Down) != 0;
            if (!pressed) Finish();
            else if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(10))
                Finish(new TimeoutException("Release Enter or Space, then invoke the command again."));
        }
        timer.Tick += Tick; owner.Unloaded += Unloaded;
        if (cancellationToken.CanBeCanceled)
            registration = cancellationToken.Register(() => owner.DispatcherQueue.TryEnqueue(() => Finish(new OperationCanceledException())));
        timer.Start();
        return completion.Task;
    }
}
