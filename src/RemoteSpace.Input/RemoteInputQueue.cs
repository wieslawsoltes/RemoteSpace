using System.Threading.Channels;
using RemoteSpace.Core;

namespace RemoteSpace.Input;

/// <summary>Bounded, ordered, single-consumer input queue. Only adjacent pure moves
/// coalesce; keys, button transitions, wheel events and synchronization are barriers.
/// RunAsync owns one reusable batch buffer until its awaited sender returns. FlushAsync
/// waits for actual send completion, not merely dequeue. Call Complete to drain.</summary>
public sealed class RemoteInputQueue
{
    private readonly object _gate = new();
    private readonly Entry[] _entries;
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private int _head, _count, _running;
    private long _accepted, _sent, _coalesced;
    private bool _complete, _stopped;
    private Exception? _failure;
    private TaskCompletionSource? _progress;
    public RemoteInputQueue(int capacity = 4096)
    {
        if (capacity is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(capacity));
        _entries = new Entry[capacity];
    }
    public int Count { get { lock (_gate) return _count; } }
    public int Capacity => _entries.Length;
    public long CoalescedMoves { get { lock (_gate) return _coalesced; } }
    public bool TryEnqueue(RemoteInput input) => TryEnqueue([input]);
    public bool TryEnqueue(ReadOnlySpan<RemoteInput> inputs)
    {
        foreach (var input in inputs) RemoteInputValidation.Validate(input);
        lock (_gate)
        {
            if (_complete || _stopped) return false;
            int required = 0;
            bool lastMove = _count > 0 && RemoteInputValidation.IsMotion(_entries[(_head + _count - 1) % Capacity].Input);
            foreach (var input in inputs)
            {
                bool move = RemoteInputValidation.IsMotion(input);
                if (!(move && lastMove)) required++;
                lastMove = move;
            }
            // Atomic failure: do not enqueue half a shortcut, surrogate pair or release list.
            if (required > Capacity - _count) return false;
            foreach (var input in inputs)
            {
                int tail = (_head + _count - 1 + Capacity) % Capacity;
                var entry = new Entry(input, ++_accepted);
                if (_count > 0 && RemoteInputValidation.IsMotion(input) && RemoteInputValidation.IsMotion(_entries[tail].Input))
                { _entries[tail] = entry; _coalesced++; }
                else { _entries[(_head + _count) % Capacity] = entry; _count++; }
            }
            if (_count > 0) _wake.Writer.TryWrite(0);
            return true;
        }
    }
    public void Complete()
    {
        lock (_gate) { _complete = true; _wake.Writer.TryWrite(0); }
    }
    public Task FlushAsync(CancellationToken ct = default)
    {
        lock (_gate) return WaitForSentAsync(_accepted, ct);
    }
    private async Task WaitForSentAsync(long target, CancellationToken ct)
    {
        while (true)
        {
            Task progress;
            lock (_gate)
            {
                if (_failure is not null) throw new IOException("Remote input was not completely sent.", _failure);
                if (_sent >= target) return;
                if (_stopped) throw new IOException("Remote input stopped before the requested flush.");
                _progress ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                progress = _progress.Task;
            }
            await progress.WaitAsync(ct).ConfigureAwait(false);
        }
    }
    public async Task RunAsync(Func<ReadOnlyMemory<RemoteInput>, CancellationToken, ValueTask> send, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (Interlocked.Exchange(ref _running, 1) != 0) throw new InvalidOperationException("The input queue has a single consumer.");
        var batch = new RemoteInput[RemoteInputValidation.MaximumBatch];
        try
        {
            while (true)
            {
                int count; long sequence;
                lock (_gate)
                {
                    count = Math.Min(batch.Length, _count); sequence = _sent;
                    if (count == 0 && _complete) return;
                    for (int i = 0; i < count; i++)
                    {
                        var entry = _entries[_head]; _entries[_head] = default;
                        _head = (_head + 1) % Capacity; _count--;
                        batch[i] = entry.Input; sequence = entry.Sequence;
                    }
                }
                if (count == 0) { await _wake.Reader.ReadAsync(ct).ConfigureAwait(false); continue; }
                ct.ThrowIfCancellationRequested();
                await send(batch.AsMemory(0, count), ct).ConfigureAwait(false);
                Array.Clear(batch, 0, count);
                lock (_gate)
                {
                    _sent = sequence; _progress?.TrySetResult(); _progress = null;
                }
            }
        }
        catch (Exception e) { lock (_gate) _failure = e; throw; }
        finally
        {
            Array.Clear(batch);
            lock (_gate)
            {
                _stopped = _complete = true; Array.Clear(_entries); _count = 0;
                _progress?.TrySetResult(); _progress = null; _wake.Writer.TryComplete();
            }
        }
    }
    private readonly record struct Entry(RemoteInput Input, long Sequence);
}
