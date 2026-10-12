namespace RemoteSpace.Core;

/// <summary>Server-confirmed display change. Subscribe before sending so a fast
/// reactivation cannot be lost. A successful send alone is not an acknowledgement.</summary>
public static class RemoteDisplayRequest
{
    public static async Task ApplyAsync(IRemoteSession session, IRemoteDisplayControl display,
        DisplayMonitor monitor, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(monitor);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        DisplayLayout.Validate([monitor], display.DisplayCapabilities ?? throw new InvalidOperationException("Remote resolution is not available."));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(timeout);
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();
        bool matched = false;
        void CheckState(SessionState state)
        {
            lock (gate)
            {
                if (state == SessionState.Active && matched) confirmed.TrySetResult();
                else if (state is SessionState.Disconnected or SessionState.Disconnecting or SessionState.Faulted)
                    confirmed.TrySetException(new IOException("The remote session ended before confirming its resolution."));
            }
        }
        void Resized(DesktopSize size)
        {
            lock (gate)
            {
                matched = size.Width == monitor.Width && size.Height == monitor.Height;
                CheckState(session.State);
            }
        }
        session.DesktopResized += Resized;
        session.StateChanged += CheckState;
        try
        {
            await SessionReadiness.WaitForActiveAsync(session, stop.Token).ConfigureAwait(false);
            await display.RequestDisplayLayoutAsync([monitor], stop.Token).ConfigureAwait(false);
            await confirmed.Task.WaitAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && stop.IsCancellationRequested)
        {
            throw new TimeoutException("The server did not confirm the requested resolution. Automatic resizing has stopped.");
        }
        finally
        {
            session.DesktopResized -= Resized;
            session.StateChanged -= CheckState;
            // Observe a terminal notification even when sending itself failed.
            if (confirmed.Task.IsFaulted) _ = confirmed.Task.Exception;
        }
    }
}
