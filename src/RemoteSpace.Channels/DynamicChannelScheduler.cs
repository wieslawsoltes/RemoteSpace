namespace RemoteSpace.Channels;

/// <summary>Bounded packet scheduler for MS-RDPEDYC v2 priority charges.
/// Nonzero classes use byte-weighted virtual finish times (bandwidth is inverse to charge).
/// Zero-charge classes bypass weighted scheduling. Call from one serialized writer.</summary>
public sealed class DynamicChannelScheduler
{
    public const int MaximumQueuedBytes = 8 * 1024 * 1024;
    private readonly Queue<(uint Channel, byte[] Packet)>[] _queues = [new(), new(), new(), new()];
    private readonly ushort[] _charges = [1, 1, 1, 1];
    private readonly decimal[] _finish = new decimal[4];
    private decimal _clock;
    private int _immediate;
    public int QueuedBytes { get; private set; }
    public void Configure(ReadOnlySpan<ushort> charges)
    {
        if (charges.Length != 4) throw new ArgumentException("Exactly four priority charges are required.", nameof(charges));
        if (QueuedBytes != 0) throw new InvalidOperationException("Cannot reconfigure queued priority traffic.");
        charges.CopyTo(_charges); Array.Clear(_finish); _clock = 0;
    }
    public void Enqueue(uint channel, byte priority, byte[][] packets)
    {
        ArgumentNullException.ThrowIfNull(packets);
        if (priority > 3) throw new ArgumentOutOfRangeException(nameof(priority));
        long bytes = packets.Sum(p => p is null ? throw new ArgumentException("Null packet.", nameof(packets)) : (long)p.Length);
        if (packets.Any(p => p.Length is < 1 or > 1600) || bytes > MaximumQueuedBytes - QueuedBytes)
            throw new ArgumentException("Dynamic-channel output queue exceeds its bound.", nameof(packets));
        if (_queues[priority].Count == 0) _finish[priority] = Math.Max(_finish[priority], _clock);
        foreach (byte[] packet in packets) _queues[priority].Enqueue((channel, (byte[])packet.Clone()));
        QueuedBytes += (int)bytes;
    }
    public bool TryDequeue(out byte[] packet)
    {
        for (int n = 0; n < 4; n++)
        {
            int p = (_immediate + n) % 4;
            if (_charges[p] == 0 && _queues[p].Count > 0) { _immediate = (p + 1) % 4; packet = Take(p); return true; }
        }
        int selected = -1; decimal next = decimal.MaxValue;
        for (int p = 0; p < 4; p++)
        {
            if (_queues[p].Count == 0) continue;
            decimal finish = _finish[p] + (decimal)_queues[p].Peek().Packet.Length * _charges[p];
            if (finish < next) { selected = p; next = finish; }
        }
        if (selected < 0) { packet = []; return false; }
        _clock = _finish[selected] = next; packet = Take(selected); return true;
    }
    private byte[] Take(int priority) { byte[] packet = _queues[priority].Dequeue().Packet; QueuedBytes -= packet.Length; return packet; }
    public void Remove(uint channel)
    {
        foreach (var queue in _queues)
        {
            int count = queue.Count;
            for (int n = 0; n < count; n++) { var item = queue.Dequeue(); if (item.Channel == channel) QueuedBytes -= item.Packet.Length; else queue.Enqueue(item); }
        }
    }
    public void Clear() { foreach (var q in _queues) q.Clear(); QueuedBytes = 0; Array.Clear(_finish); _clock = 0; }
}
