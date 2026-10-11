namespace RemoteSpace.Core;

/// <summary>Wait across a server reactivation without dropping queued input. The host
/// supplies cancellation; terminal sessions fail rather than hanging indefinitely.</summary>
public static class SessionReadiness
{
    public static async ValueTask WaitForActiveAsync(IRemoteSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ct.ThrowIfCancellationRequested();
        if (session.State == SessionState.Active) return;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(SessionState state)
        {
            if (state == SessionState.Active) ready.TrySetResult(true);
            else if (state is SessionState.Disconnected or SessionState.Disconnecting or SessionState.Faulted) ready.TrySetResult(false);
        }
        session.StateChanged += Changed;
        try
        {
            Changed(session.State); // Recheck after subscribing: no activation wake-up race.
            if (!await ready.Task.WaitAsync(ct).ConfigureAwait(false)) throw new IOException("Remote session ended before input could be sent.");
        }
        finally { session.StateChanged -= Changed; }
    }
}
