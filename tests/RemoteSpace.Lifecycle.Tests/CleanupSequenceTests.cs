using RemoteSpace.Core;

internal static class CleanupSequenceTests
{
    public static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAILED: " + name);
            checks++; Console.WriteLine("PASS " + name);
        }
        static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        Check(ReferenceEquals(AsyncCleanupSequence.RunAsync(), Task.CompletedTask), "empty cleanup uses completed task");
        int calls = 0;
        Func<ValueTask> count = () => { calls++; return ValueTask.CompletedTask; };
        try { _ = AsyncCleanupSequence.RunAsync(null!); throw new Exception("Expected invalid array"); }
        catch (ArgumentNullException) { Check(true, "null cleanup array is rejected synchronously"); }
        try { _ = AsyncCleanupSequence.RunAsync(count, null!); throw new Exception("Expected invalid callback"); }
        catch (ArgumentNullException) { Check(calls == 0, "all callbacks validate before any cleanup side effect"); }

        for (int failing = 0; failing < 8; failing++)
        {
            var order = new List<int>();
            var expected = new IOException("step " + failing);
            Func<ValueTask>[] steps = Enumerable.Range(0, 8).Select(index => (Func<ValueTask>)(() =>
            {
                order.Add(index);
                if (index == failing) throw expected;
                return ValueTask.CompletedTask;
            })).ToArray();
            Exception? failure = null;
            try { await AsyncCleanupSequence.RunAsync(steps); }
            catch (Exception error) { failure = error; }
            Check(ReferenceEquals(failure, expected), "single cleanup failure preserves identity at step " + failing);
            Check(order.SequenceEqual(Enumerable.Range(0, 8)), "failure at step " + failing + " does not skip later resource owners");
        }

        var entered = Signal(); var release = Signal(); var stages = new List<int>();
        Func<ValueTask>[] callbacks = [
            async () => { stages.Add(1); entered.SetResult(); await release.Task; stages.Add(2); },
            () => { stages.Add(3); return ValueTask.CompletedTask; }
        ];
        var pending = AsyncCleanupSequence.RunAsync(callbacks);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!pending.IsCompleted && stages.SequenceEqual([1]), "cleanup drains asynchronous stage before starting the next");
        callbacks[1] = () => throw new Exception("Caller replaced callback after snapshot");
        release.SetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Check(stages.SequenceEqual([1, 2, 3]), "pending cleanup uses owned callback snapshot rather than mutable caller array");

        var first = new IOException("first"); var second = new OperationCanceledException("cancelled"); var third = new InvalidOperationException("third");
        calls = 0;
        try
        {
            await AsyncCleanupSequence.RunAsync(
                () => throw first,
                async () => { await Task.Yield(); throw second; },
                () => new ValueTask(Task.FromException(third)),
                count);
            throw new Exception("Expected aggregate");
        }
        catch (AggregateException error)
        {
            Check(error.InnerExceptions.SequenceEqual([first, second, third]), "synchronous asynchronous and cancelled failures retain their order");
            Check(calls == 1, "cancellation does not suppress final release");
        }
        calls = 0;
        try { await AsyncCleanupSequence.RunAsync(() => throw second, count); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException error)
        {
            Check(ReferenceEquals(error, second) && calls == 1, "single cancellation is rethrown only after all cleanup");
        }
        await AsyncCleanupSequence.RunAsync(count, count);
        Check(calls == 3, "successful steps each run once without retries");

        // Exercise the actual UI-affinity contract on a dedicated thread. The
        // first await completes off-thread; the next callback must return to
        // the original pump rather than a pool thread.
        Exception? pumpFailure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var context = new PumpContext();
                SynchronizationContext.SetSynchronizationContext(context);
                int owner = Environment.CurrentManagedThreadId;
                var gate = Signal();
                var task = AsyncCleanupSequence.RunAsync(
                    async () => { await gate.Task; Check(Environment.CurrentManagedThreadId == owner, "asynchronous callback keeps owner context"); },
                    () => { Check(Environment.CurrentManagedThreadId == owner, "later cleanup callback keeps owner context"); return ValueTask.CompletedTask; });
                ThreadPool.QueueUserWorkItem(_ => gate.SetResult());
                context.RunUntil(task); task.GetAwaiter().GetResult();
            }
            catch (Exception error) { pumpFailure = error; }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        }) { IsBackground = true };
        thread.Start();
        Check(thread.Join(TimeSpan.FromSeconds(10)), "UI-context cleanup pump terminates");
        if (pumpFailure is not null) throw new Exception("Cleanup context test failed", pumpFailure);
        Console.WriteLine($"RESULT: {checks} cleanup sequence checks passed.");
    }

    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));
        public void RunUntil(Task task)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!task.IsCompleted)
            {
                if (_queue.TryTake(out var work, 50)) work.Callback(work.State);
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Cleanup pump stalled.");
            }
        }
        public void Dispose() => _queue.Dispose();
    }
}
