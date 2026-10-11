namespace RemoteSpace.Core;

/// <summary>Single-use start/dispose arbitration. Completion is signaled by the
/// session's outermost finally, even when a consumer's event handler throws.
/// Disposal invokes stop once and all callers observe the same completion task.</summary>
public sealed class SessionLifetime
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started, _disposing;
    private Task? _disposal;
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            if (_started) throw new InvalidOperationException("Create a new session object to reconnect.");
            _started = true;
        }
    }
    public void Finish() => _finished.TrySetResult();
    public ValueTask DisposeAsync(Func<ValueTask> stop, Action release)
    {
        ArgumentNullException.ThrowIfNull(stop); ArgumentNullException.ThrowIfNull(release);
        TaskCompletionSource? owner = null; Task disposal; bool started;
        lock (_gate)
        {
            if (_disposal is null)
            {
                owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposal = owner.Task; _disposing = true;
            }
            disposal = _disposal; started = _started;
        }
        if (owner is not null) _ = DisposeCoreAsync(owner, started, stop, release);
        return new(disposal);
    }
    private async Task DisposeCoreAsync(TaskCompletionSource completion, bool started, Func<ValueTask> stop, Action release)
    {
        Exception? failure = null;
        try { await stop().ConfigureAwait(false); } catch (Exception e) { failure = e; }
        try { if (started) await _finished.Task.ConfigureAwait(false); }
        finally
        {
            try { release(); } catch (Exception e) { failure ??= e; }
            if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
        }
    }
}
