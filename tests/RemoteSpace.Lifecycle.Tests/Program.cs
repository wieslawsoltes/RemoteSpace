using RemoteSpace.Client;
using RemoteSpace.Core;
using RemoteSpace.Transport;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); checks++; Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> operation, string name)
{
    bool rejected = false;
    try { await operation().WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (Exception e) when (e is InvalidOperationException or IOException or ArgumentException or OperationCanceledException) { rejected = true; }
    Check(rejected, name);
}
var lifetime = new SessionLifetime(); lifetime.Start();
int stopped = 0, released = 0;
var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
ValueTask Stop() { Interlocked.Increment(ref stopped); stopEntered.TrySetResult(); return ValueTask.CompletedTask; }
void Release() => Interlocked.Increment(ref released);
Task[] callers = Enumerable.Range(0, 64).Select(_ => Task.Run(async () => await lifetime.DisposeAsync(Stop, Release))).ToArray();
await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(released == 0, "concurrent disposal waits for the running session to finish");
lifetime.Finish(); await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(5));
Check(stopped == 1 && released == 1, "concurrent callers stop and release exactly once");
await Reject(() => { lifetime.Start(); return Task.CompletedTask; }, "disposed lifetime cannot restart");
var beforeStart = new SessionLifetime(); int releaseBefore = 0;
await beforeStart.DisposeAsync(() => ValueTask.CompletedTask, () => releaseBefore++);
await beforeStart.DisposeAsync(() => throw new Exception("Repeated stop"), () => throw new Exception("Repeated release"));
Check(releaseBefore == 1, "disposing an unstarted session is immediate and idempotent");
var throwing = new SessionLifetime(); throwing.Start();
Task failed = throwing.DisposeAsync(() => throw new InvalidOperationException("observer"), () => released++).AsTask();
Check(!failed.IsCompleted, "stop failure still waits for run completion");
throwing.Finish(); await Reject(() => failed, "stop failure remains observable after cleanup");
Check(released == 2, "release executes even when stop throws");
var releaseThrows = new SessionLifetime();
Task first = releaseThrows.DisposeAsync(() => ValueTask.CompletedTask, () => throw new InvalidOperationException("release")).AsTask();
Task second = releaseThrows.DisposeAsync(() => throw new Exception("must not retry"), () => {}).AsTask();
Check(ReferenceEquals(first, second), "failed disposal returns the same task to every caller");
await Reject(() => first, "release failure is observable");
var reentrant = new SessionLifetime(); Task? nested = null;
Task outer = reentrant.DisposeAsync(() => { nested = reentrant.DisposeAsync(() => throw new Exception("reentered stop"), () => {}).AsTask(); return ValueTask.CompletedTask; }, () => {}).AsTask();
await outer;
Check(ReferenceEquals(outer, nested), "reentrant observer receives the already-published disposal task");

// These are real session classes. The observer throws at Connecting before a TCP
// or WebSocket connection is attempted: no external host, credentials or test seam.
foreach (bool bridge in new[] { false, true })
{
    string kind = bridge ? "bridge" : "native";
    Func<IRemoteSession> factory = () => bridge ? new BridgeSession(new Uri("ws://127.0.0.1:1/session"), new string('x', 32)) : new RdpSession();
    var session = factory();
    session.StateChanged += _ => throw new InvalidOperationException("consumer callback");
    await Reject(() => session.RunAsync(new() { Host = "127.0.0.1" }, new("unused", "unused")), kind + " observer exception propagates from RunAsync");
    await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    await Task.WhenAll(Enumerable.Range(0,32).Select(_ => session.DisposeAsync().AsTask())).WaitAsync(TimeSpan.FromSeconds(5));
    Check(true, kind + " callback failure cannot strand DisposeAsync completion");
    await Reject(() => session.RunAsync(new() { Host = "127.0.0.1" }, new("unused", "unused")), kind + " disposed session cannot reconnect");
    await session.DisconnectAsync();
    Check(true, kind + " disconnect after disposal is safe");
    var unstarted = factory(); await unstarted.DisposeAsync(); await unstarted.DisposeAsync();
    await Reject(() => unstarted.RunAsync(new() { Host = "127.0.0.1" }, new("unused", "unused")), kind + " cannot start after pre-run disposal");
    var cancelled = factory(); using var stop = new CancellationTokenSource(); stop.Cancel();
    await cancelled.RunAsync(new() { Host = "127.0.0.1" }, new("unused", "unused"), stop.Token).WaitAsync(TimeSpan.FromSeconds(5));
    await cancelled.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    Check(true, kind + " pre-cancelled session cleanup completes");
    var terminalThrows = factory();
    terminalThrows.StateChanged += state => { if (state == SessionState.Disconnected) throw new InvalidOperationException("terminal observer"); };
    await Reject(() => terminalThrows.RunAsync(new() { Host = "127.0.0.1" }, new("unused", "unused"), stop.Token), kind + " terminal observer failure propagates");
    await terminalThrows.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    Check(true, kind + " terminal observer failure cannot strand disposal");
}
Console.WriteLine($"RESULT: {checks} lifecycle checks passed.");

await TrafficTests.RunAsync();
