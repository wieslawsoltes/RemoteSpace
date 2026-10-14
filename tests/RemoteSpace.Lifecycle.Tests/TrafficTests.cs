using System.Runtime.CompilerServices;
using RemoteSpace.Client;
using RemoteSpace.Core;
using RemoteSpace.Protocol;
using RemoteSpace.Transport;

internal static class TrafficTests
{
    internal static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAILED traffic: " + name); checks++; }
        void Reject(Action action)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "invalid argument rejected");
        }
        var counter = new SessionTrafficCounter(SessionTrafficLayer.TcpStream);
        Check(counter.Snapshot == new SessionTrafficSnapshot(SessionTrafficLayer.TcpStream, 0, 0), "initial snapshot");
        Reject(() => counter.AddReceived(-1)); Reject(() => counter.AddSent(-1));
        Reject(() => new SessionTrafficCounter(SessionTrafficLayer.Unavailable));
        Reject(() => new SessionTrafficCounter((SessionTrafficLayer)99));
        Parallel.For(0, 100000, _ => { counter.AddReceived(3); counter.AddSent(7); });
        Check(counter.Snapshot.ReceivedBytes == 300000 && counter.Snapshot.SentBytes == 700000, "concurrent increments lose no bytes");
        var sampler = new SessionTrafficSampler();
        var initial = new SessionTrafficSnapshot(SessionTrafficLayer.TcpStream, 100, 50);
        Check(!sampler.Sample(initial, 1000).HasInterval, "first sample has no invented rate");
        var next = initial with { ReceivedBytes = 3100, SentBytes = 2050 };
        var rates = sampler.Sample(next, 3000);
        Check(rates == new SessionTrafficRates(true, 1500, 1000), "actual elapsed interval, not assumed poll period");
        Check(!sampler.Sample(next with { ReceivedBytes = 4100 }, 3000).HasInterval, "duplicate timestamp");
        Check(!sampler.Sample(next with { ReceivedBytes = 4100 }, 2000).HasInterval, "out-of-order timestamp");
        Check(sampler.Sample(next with { ReceivedBytes = 5100 }, 4000).ReceivedBytesPerSecond == 2000, "duplicate sample doesn't consume bytes");
        Check(sampler.Sample(next with { ReceivedBytes = 5100 }, 5000) == new SessionTrafficRates(true, 0, 0), "idle interval reports zero not stale rate");
        Check(!sampler.Sample(initial, 6000).HasInterval, "decreasing counters rebaseline");
        Check(!sampler.Sample(initial with { Layer = SessionTrafficLayer.WebSocketPayload }, 7000).HasInterval, "different observation layers never compare");
        Check(!sampler.Sample(default, 8000).HasInterval && !sampler.Sample(initial, 9000).HasInterval, "unavailable clears baseline");
        sampler.Reset();
        Check(!sampler.Sample(initial, long.MaxValue - 1).HasInterval, "large clock baseline");
        Check(sampler.Sample(initial with { SentBytes = 51 }, long.MaxValue).SentBytesPerSecond == 1000, "one millisecond near long max is precise");
        sampler.Reset(); sampler.Sample(initial, long.MinValue);
        Check(double.IsFinite(sampler.Sample(next, long.MaxValue).ReceivedBytesPerSecond), "extreme elapsed interval cannot overflow");
        Reject(() => sampler.Sample(initial with { ReceivedBytes = -1 }, 0));
        Reject(() => sampler.Sample(initial with { Layer = (SessionTrafficLayer)99 }, 0));

        var bytes = Enumerable.Range(0, 96).Select(i => (byte)i).ToArray();
        var meter = new SessionTrafficCounter(SessionTrafficLayer.TcpStream);
        using var memory = new MemoryStream();
        await using (var stream = new TrafficCountingStream(memory, meter, leaveOpen: true))
        {
            stream.Write(bytes, 1, 10); stream.Write(bytes.AsSpan(11, 10)); stream.WriteByte(bytes[21]);
            await stream.WriteAsync(bytes, 22, 10); await stream.WriteAsync(bytes.AsMemory(32, 10));
            Check(meter.Snapshot.SentBytes == 41 && meter.Snapshot.ReceivedBytes == 0, "all write overloads count once");
            stream.Position = 0;
            var result = new byte[41];
            Check(stream.Read(result, 0, 7) == 7 && stream.Read(result.AsSpan(7, 7)) == 7, "sync reads preserve count");
            result[14] = (byte)stream.ReadByte();
            Check(await stream.ReadAsync(result, 15, 7) == 7 && await stream.ReadAsync(result.AsMemory(22)) == 19, "async reads preserve count");
            Check(result.SequenceEqual(bytes.Skip(1).Take(41)), "payload is not changed or copied out of order");
            Check(stream.ReadByte() == -1 && await stream.ReadAsync(result.AsMemory()) == 0, "EOF is not an extra byte");
            Check(meter.Snapshot.ReceivedBytes == 41, "received counts actual short reads");
            Reject(() => { int unexpected = stream.ReadAsync(null!, 0, 0, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception($"Null buffer read succeeded: {unexpected}."); });
            Reject(() => stream.WriteAsync(null!, 0, 0, CancellationToken.None));
            await stream.FlushAsync(); stream.SetLength(42); stream.Seek(0, SeekOrigin.Begin);
            Check(meter.Snapshot.SentBytes == 41, "seek length flush are not traffic");
            using var copied = new MemoryStream(); await stream.CopyToAsync(copied);
            Check(meter.Snapshot.ReceivedBytes == 83 && copied.Length == 42, "CopyToAsync uses counted reads once");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            bool stopped = false;
            try { await stream.WriteAsync(bytes.AsMemory(), canceled.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped && meter.Snapshot.SentBytes == 41, "pre-cancelled write isn't recorded");
        }
        Check(memory.CanRead, "leave-open ownership");
        var owned = new ObservedStream();
        var ownedMeter = new SessionTrafficCounter(SessionTrafficLayer.TcpStream);
        var wrapper = new TrafficCountingStream(owned, ownedMeter);
        var pending = wrapper.WriteAsync(bytes.AsMemory());
        Check(!pending.IsCompleted && ownedMeter.Snapshot.SentBytes == 0, "incomplete write is not delivered traffic");
        owned.WriteCompletion.SetResult(); await pending;
        Check(ownedMeter.Snapshot.SentBytes == 96, "asynchronous write counted after completion");
        var readBuffer = new byte[10]; var read = wrapper.ReadAsync(readBuffer.AsMemory());
        Check(!read.IsCompleted && ownedMeter.Snapshot.ReceivedBytes == 0, "incomplete read is not traffic");
        owned.ReadCompletion.SetResult(3);
        Check(await read == 3 && ownedMeter.Snapshot.ReceivedBytes == 3, "asynchronous short read");
        owned.Fail = true;
        bool failed = false;
        try { await wrapper.WriteAsync(bytes.AsMemory()); } catch (IOException) { failed = true; }
        Check(failed && ownedMeter.Snapshot.SentBytes == 96, "failed write doesn't claim full buffer delivered");
        failed = false;
        try { int unexpected = await wrapper.ReadAsync(readBuffer.AsMemory()); throw new Exception($"Expected read failure, received {unexpected} bytes."); } catch (IOException) { failed = true; }
        Check(failed && ownedMeter.Snapshot.ReceivedBytes == 3, "failed read isn't counted");
        await wrapper.DisposeAsync(); wrapper.Dispose(); await wrapper.DisposeAsync();
        Check(owned.DisposeCalls == 1 && !wrapper.CanRead && !wrapper.CanWrite, "underlying owner disposed exactly once");
        bool disposed = false;
        try { wrapper.ReadByte(); } catch (ObjectDisposedException) { disposed = true; }
        Check(disposed, "disposed wrapper rejects reads");
        await using var direct = new RdpSession();
        await using var bridge = new BridgeSession(new Uri("ws://127.0.0.1:1/session"), new string('x', 32));
        Check(((IRemoteSessionTraffic)direct).Traffic.Layer == SessionTrafficLayer.TcpStream, "native transport exposes correct layer");
        Check(((IRemoteSessionTraffic)bridge).Traffic.Layer == SessionTrafficLayer.WebSocketPayload, "bridge exposes payload not raw network bytes");
        _ = AllocationKernel();
        long before = GC.GetAllocatedBytesForCurrentThread();
        long checksum = AllocationKernel();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // The kernel owns its counters/sampler once. Measure the warmed hot loop separately.
        Check(checksum > 0 && allocated < 1024, "bounded one-time measurement objects");
        Console.WriteLine($"PASS {checks} traffic counter, rate, stream and ownership regressions.");
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static long AllocationKernel()
    {
        var counter = new SessionTrafficCounter(SessionTrafficLayer.TcpStream);
        var sampler = new SessionTrafficSampler(); sampler.Sample(counter.Snapshot, 0);
        long before = GC.GetAllocatedBytesForCurrentThread(); double sum = 0;
        for (int i = 1; i <= 100000; i++)
        {
            counter.AddReceived(100); counter.AddSent(20);
            sum += sampler.Sample(counter.Snapshot, i).ReceivedBytesPerSecond;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (allocated != 0 || sum != 10000000000d) throw new Exception($"Traffic hot loop: {allocated} bytes, checksum {sum}.");
        return (long)sum;
    }
    private sealed class ObservedStream : MemoryStream
    {
        internal readonly TaskCompletionSource WriteCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<int> ReadCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Fail;
        internal int DisposeCalls;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Fail ? ValueTask.FromException(new IOException("fixture")) : new(WriteCompletion.Task);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Fail ? ValueTask.FromException<int>(new IOException("fixture")) : new(ReadCompletion.Task);
        protected override void Dispose(bool disposing) { if (disposing) DisposeCalls++; base.Dispose(disposing); }
    }
}
