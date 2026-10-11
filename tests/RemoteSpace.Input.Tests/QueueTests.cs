using RemoteSpace.Core;
using RemoteSpace.Input;

internal static class QueueTests
{
    internal static async Task Run(Action<bool,string> check)
    {
        RemoteInput Move(ushort x) => new(InputKind.Pointer,0x0800,0,x,0);
        var key = new RemoteInput(InputKind.ScanCode,0,30);
        var queue = new RemoteInputQueue(8);
        for (int i=0;i<10000;i++) if (!queue.TryEnqueue(Move((ushort)i))) throw new Exception("Motion queue overflow");
        check(queue.Count==1 && queue.CoalescedMoves==9999,"10,000 consecutive moves retain one final coordinate");
        queue.TryEnqueue(key); queue.TryEnqueue(Move(8)); queue.TryEnqueue(Move(9));
        queue.TryEnqueue(key with {Flags=0x8000});
        var output = new List<RemoteInput>(); queue.Complete();
        await queue.RunAsync((items,_)=>{ output.AddRange(items.ToArray());return ValueTask.CompletedTask; });
        check(output.SequenceEqual(new[]{Move(9999),key,Move(9),key with{Flags=0x8000}}),"coalescing never crosses key transitions");
        await queue.FlushAsync(); check(!queue.TryEnqueue(key),"completed queue rejects later input");
        var bounded = new RemoteInputQueue(2); bounded.TryEnqueue(key);
        check(!bounded.TryEnqueue(new[]{key,key}) && bounded.Count==1,"batch capacity failure is atomic");
        bounded.TryEnqueue(key with{Flags=0x8000}); bounded.Complete();
        output.Clear(); await bounded.RunAsync((items,_)=>{output.AddRange(items.ToArray());return ValueTask.CompletedTask;});
        check(output.Count==2 && output[1].Flags==0x8000,"failed oversized offer leaves space for release");
        var blocked = new RemoteInputQueue(); blocked.TryEnqueue(key);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run=blocked.RunAsync(async (items,ct)=>{entered.TrySetResult();await resume.Task.WaitAsync(ct);});
        await entered.Task;Task flush=blocked.FlushAsync();
        check(!flush.IsCompleted,"flush waits for actual sender completion");
        blocked.Complete();resume.SetResult();await run;await flush;
        var cancel = new RemoteInputQueue(); cancel.TryEnqueue(key);using var stop = new CancellationTokenSource();
        var sending=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task cancelled=cancel.RunAsync(async (items,ct)=>{sending.TrySetResult();await Task.Delay(Timeout.Infinite,ct);},stop.Token);
        await sending.Task;Task notSent=cancel.FlushAsync();stop.Cancel();
        try{await cancelled;}catch(OperationCanceledException){}
        bool failed=false;try{await notSent;}catch(IOException){failed=true;}
        check(failed && cancel.Count==0,"cancelled sender releases queue memory and fails pending flush");
        var concurrent = new RemoteInputQueue(8192); int received=0; int maxBatch=0;
        Task consumer=concurrent.RunAsync((items,_)=>{received+=items.Length;maxBatch=Math.Max(maxBatch,items.Length);return ValueTask.CompletedTask;});
        await Task.WhenAll(Enumerable.Range(0,4).Select(_=>Task.Run(()=>
        {
            for(int i=0;i<1000;i++) if(!concurrent.TryEnqueue(key))throw new Exception("Unexpected bounded overflow");
        })));
        concurrent.Complete();await consumer;
        check(received==4000 && maxBatch<=64,"concurrent producers preserve all non-motion events and batch bound");
        var inactive = new FakeSession();
        Task readiness = SessionReadiness.WaitForActiveAsync(inactive).AsTask();
        check(!readiness.IsCompleted && inactive.Subscribers==1,"reactivation waits rather than discarding queued input");
        inactive.Change(SessionState.Active);await readiness;
        check(inactive.Subscribers==0,"reactivation waiter unsubscribes after success");
        inactive.Change(SessionState.Establishing);using var cancelReady=new CancellationTokenSource();
        readiness=SessionReadiness.WaitForActiveAsync(inactive,cancelReady.Token).AsTask();cancelReady.Cancel();
        try{await readiness;}catch(OperationCanceledException){}
        check(inactive.Subscribers==0,"reactivation waiter unsubscribes after cancellation");
        readiness=SessionReadiness.WaitForActiveAsync(inactive).AsTask();inactive.Change(SessionState.Disconnected);
        bool terminal=false;try{await readiness;}catch(IOException){terminal=true;}
        check(terminal && inactive.Subscribers==0,"terminal session fails pending input readiness without hanging");
        // Each button/wheel edge is a barrier, even when coordinates are unchanged.
        var barriers = new RemoteInputQueue();
        var sequence = new[]{Move(1),new RemoteInput(InputKind.Pointer,0x9000,0,1,0),Move(2),
            new RemoteInput(InputKind.Pointer,0x0278,0),Move(3),new RemoteInput(InputKind.Pointer,0x1000,0,3,0)};
        barriers.TryEnqueue(sequence);barriers.Complete();output.Clear();
        await barriers.RunAsync((items,_)=>{output.AddRange(items.ToArray());return ValueTask.CompletedTask;});
        check(output.SequenceEqual(sequence),"mouse buttons and wheel events are ordering barriers");
    }
}

internal sealed class FakeSession : IRemoteSession
{
    private Action<SessionState>? _state;
    public int Subscribers => _state?.GetInvocationList().Length ?? 0;
    public SessionState State { get; private set; } = SessionState.Establishing;
    public event Action<SessionState>? StateChanged { add=>_state+=value; remove=>_state-=value; }
    public event Action<DesktopSize>? DesktopResized { add{} remove{} }
    public event Action<SurfaceUpdate>? SurfaceUpdated { add{} remove{} }
    public event Action<SessionDiagnostic>? Diagnostic { add{} remove{} }
    public event Action<string>? ClipboardReceived { add{} remove{} }
    public void Change(SessionState state) { State=state;_state?.Invoke(state); }
    public Task RunAsync(ConnectionOptions options,SessionCredentials credentials,CancellationToken ct=default)=>throw new NotSupportedException();
    public ValueTask SendInputAsync(RemoteInput input,CancellationToken ct=default)=>ValueTask.CompletedTask;
    public ValueTask SendClipboardAsync(string text,CancellationToken ct=default)=>ValueTask.CompletedTask;
    public ValueTask DisconnectAsync(CancellationToken ct=default) { Change(SessionState.Disconnected);return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}
