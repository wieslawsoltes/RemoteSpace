using System.Runtime.CompilerServices;
using RemoteSpace.Core;

internal static class AutomaticResizeTests
{
    internal static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAILED " + name); checks++; }
        var caps = new DisplayCapabilities(1, DisplayLayout.MaximumArea);
        foreach (var (w, h) in new[] { (200d, 200d), (201.9, 300.8), (8192d, 200d), (4096d, 4096d) })
        {
            Check(DisplayResizeTarget.TryFromViewport(w, h, caps, out var target), "bounded size accepted");
            Check(target.Width == ((int)w & ~1) && target.Height == (int)h, "whole even-width remote pixels");
            DisplayLayout.Validate([new DisplayMonitor(0, 0, target.Width, target.Height)], caps);
        }
        foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 0, 199.99, 8193, double.MaxValue })
        {
            Check(!DisplayResizeTarget.TryFromViewport(bad, 720, caps, out _), "invalid width rejected before cast");
            Check(!DisplayResizeTarget.TryFromViewport(1280, bad, caps, out _), "invalid height rejected before cast");
        }
        Check(!DisplayResizeTarget.TryFromViewport(8192, 8192, caps, out _), "allocation ceiling");
        Check(!DisplayResizeTarget.TryFromViewport(1280, 720, new(1, 1000), out _), "server area ceiling");
        Check(!DisplayResizeTarget.TryFromViewport(1280, 720, new(0, DisplayLayout.MaximumArea), out _), "server monitor ceiling");
        Check(!DisplayResizeTarget.TryFromViewport(1280, 720, null, out _), "unnegotiated server");
        var q = new DisplayResizeQueue();
        q.Offer(1280, 720, caps, 0); q.SetAvailable(true, 0);
        Check(q.DelayMilliseconds(1000, 800, 600) is null, "default is opt-out and timer-free");
        q.SetEnabled(true, 1000);
        Check(!q.TryBegin(1249, 800, 600, out _), "settles before sending");
        Check(q.TryBegin(1250, 800, 600, out var first), "starts after settle");
        for (int i = 0; i < 10000; i++) q.Offer(1400 + (i % 100), 900, caps, 1300 + i);
        Check(q.IsBusy && q.StartedRequests == 1 && q.DelayMilliseconds(20000, 1280, 720) is null, "burst retains only latest while busy");
        q.SetEnabled(false, 20000); q.SetEnabled(true, 20000);
        Check(!q.TryBegin(30000, 1280, 720, out _), "toggle cannot overlap an in-flight request");
        Check(!q.Complete(first.Ticket + 1, true, 30000) && q.IsBusy, "stale completion ignored");
        Check(q.Complete(first.Ticket, true, 30000), "confirmation completes request");
        Check(!q.TryBegin(30249, 1280, 720, out _), "rate bounded after confirmation");
        Check(q.TryBegin(30250, 1280, 720, out var second) && second.Target == new DisplayResizeTarget(1498, 900), "only newest target follows confirmation");
        q.Complete(second.Ticket, true, 30500);
        Check(q.DelayMilliseconds(31000, 1498, 900) is null, "confirmed unchanged geometry has no timer");
        q.Offer(1600, 900, caps, 31000); q.SetAvailable(false, 31000);
        Check(q.DelayMilliseconds(40000, 1498, 900) is null, "hidden paused inactive has no timer");
        q.SetAvailable(true, 40000);
        Check(!q.TryBegin(40249, 1498, 900, out _), "resume settles latest viewport");
        Check(q.TryBegin(40250, 1498, 900, out var resumed), "resume sends after settle");
        q.Complete(resumed.Ticket, false, 41000);
        Check(!q.Enabled && q.DelayMilliseconds(50000, 1498, 900) is null, "failure disables instead of retrying");
        q.SetEnabled(true, 50000); q.Offer(0, 0, caps, 50000);
        Check(q.DelayMilliseconds(60000, 1498, 900) is null, "minimized size cancels pending target");
        q.Offer(1600, 900, caps, 60000); q.Offer(1498, 900, caps, 60100);
        Check(q.DelayMilliseconds(61000, 1498, 900) is null, "returning to actual size cancels queued resize");
        q.Offer(1498, 900, caps, 61000);
        _ = OfferRepeated(q, caps);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int changed = OfferRepeated(q, caps);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(changed == 0 && allocated == 0, "100000 repeated offers have zero allocation and no work");

        using var fake = new DisplayFixture();
        fake.Send = m => { fake.Resize(m.Width, m.Height); return ValueTask.CompletedTask; };
        await RemoteDisplayRequest.ApplyAsync(fake, fake, new(0,0,1024,768), TimeSpan.FromSeconds(1));
        Check(fake.Subscriptions == 0, "synchronous confirmation is not lost; listeners removed");
        fake.Send = m => { fake.ChangeState(SessionState.Establishing); fake.Resize(m.Width,m.Height); return ValueTask.CompletedTask; };
        var waiting = RemoteDisplayRequest.ApplyAsync(fake, fake, new(0,0,1152,864), TimeSpan.FromSeconds(1));
        Check(!waiting.IsCompleted, "geometry alone during reactivation does not complete");
        fake.ChangeState(SessionState.Active); await waiting;
        fake.Send = _ => ValueTask.CompletedTask;
        using var cancel = new CancellationTokenSource();
        waiting = RemoteDisplayRequest.ApplyAsync(fake, fake, new(0,0,1600,900), TimeSpan.FromSeconds(1), cancel.Token);
        fake.Resize(800,600);
        Check(!waiting.IsCompleted, "send and wrong-size reply do not count as confirmation");
        cancel.Cancel();
        try { await waiting; throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { checks++; }
        Check(fake.Subscriptions == 0, "cancelled wait unsubscribes");
        try { await RemoteDisplayRequest.ApplyAsync(fake, fake, new(0,0,1600,900), TimeSpan.FromMilliseconds(20)); throw new Exception("Timeout ignored"); }
        catch (TimeoutException) { checks++; }
        Check(fake.Subscriptions == 0, "timeout unsubscribes");
        fake.Send = _ => { fake.ChangeState(SessionState.Faulted); return ValueTask.CompletedTask; };
        try { await RemoteDisplayRequest.ApplyAsync(fake, fake, new(0,0,1600,900), TimeSpan.FromSeconds(1)); throw new Exception("Terminal state ignored"); }
        catch (IOException) { checks++; }
        Check(fake.Subscriptions == 0, "terminal wait unsubscribes");
        fake.ChangeState(SessionState.Active);
        fake.Send = _ => throw new IOException("Fixture send failure");
        try { await RemoteDisplayRequest.ApplyAsync(fake, fake, new(0,0,1600,900), TimeSpan.FromSeconds(1)); throw new Exception("Send failure ignored"); }
        catch (IOException) { checks++; }
        Check(fake.Subscriptions == 0, "send failure unsubscribes");
        Console.WriteLine($"PASS {checks} automatic-resize planning/confirmation regressions; 100000 unchanged offers allocated {allocated} bytes.");
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int OfferRepeated(DisplayResizeQueue q, DisplayCapabilities caps)
    {
        int changed = 0;
        for (int i = 0; i < 100000; i++) if (q.Offer(1498, 900, caps, 61000 + i)) changed++;
        return changed;
    }

    private sealed class DisplayFixture : IRemoteSession, IRemoteDisplayControl, IDisposable
    {
        private Action<SessionState>? _state;
        private Action<DesktopSize>? _size;
        public int Subscriptions => (_state?.GetInvocationList().Length ?? 0) + (_size?.GetInvocationList().Length ?? 0);
        public SessionState State { get; private set; } = SessionState.Active;
        public DisplayCapabilities? DisplayCapabilities => new(1, DisplayLayout.MaximumArea);
        public Func<DisplayMonitor,ValueTask> Send { get; set; } = _ => ValueTask.CompletedTask;
        public event Action<SessionState>? StateChanged { add => _state += value; remove => _state -= value; }
        public event Action<DesktopSize>? DesktopResized { add => _size += value; remove => _size -= value; }
        public event Action<DisplayCapabilities?>? DisplayCapabilitiesChanged { add { } remove { } }
        public event Action<SurfaceUpdate>? SurfaceUpdated { add { } remove { } }
        public event Action<SessionDiagnostic>? Diagnostic { add { } remove { } }
        public event Action<string>? ClipboardReceived { add { } remove { } }
        public void Resize(int w,int h) => _size?.Invoke(new(w,h));
        public void ChangeState(SessionState s) { State=s; _state?.Invoke(s); }
        public ValueTask RequestDisplayLayoutAsync(IReadOnlyList<DisplayMonitor> monitors, CancellationToken cancellationToken = default) => Send(monitors[0]);
        public Task RunAsync(ConnectionOptions options, SessionCredentials credentials, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask SendInputAsync(RemoteInput input, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SendClipboardAsync(string text, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }
}
