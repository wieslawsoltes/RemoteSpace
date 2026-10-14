using System.Runtime.ExceptionServices;

namespace RemoteSpace.Core;

/// <summary>Runs every cleanup step in order, even after failure or cancellation.
/// The caller owns single-operation admission and termination of pending work.
/// This helper never retries, starts parallel cleanup, or abandons a step.</summary>
public static class AsyncCleanupSequence
{
    /// <summary>Snapshots and validates the callbacks before running any of them.
    /// Continuations retain the caller's synchronization context so UI-affine
    /// cleanup can safely follow asynchronous transport cleanup. Every callback
    /// must finish on its own; cancellation is a failure to report, not a reason
    /// to skip releasing the remaining resources.</summary>
    public static Task RunAsync(params Func<ValueTask>[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Length == 0) return Task.CompletedTask;
        var snapshot = (Func<ValueTask>[])steps.Clone();
        foreach (var step in snapshot) ArgumentNullException.ThrowIfNull(step);
        return RunCoreAsync(snapshot);
    }

    private static async Task RunCoreAsync(Func<ValueTask>[] steps)
    {
        List<Exception>? failures = null;
        foreach (var step in steps)
        {
            try { await step(); }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        if (failures is { Count: 1 }) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is { Count: > 1 }) throw new AggregateException("Multiple cleanup steps failed.", failures);
    }
}
