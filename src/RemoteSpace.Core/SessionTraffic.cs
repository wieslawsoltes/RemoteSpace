namespace RemoteSpace.Core;

/// <summary>The observation layer, not the negotiated RDP codec or estimated link capacity.</summary>
public enum SessionTrafficLayer { Unavailable, TcpStream, WebSocketPayload }

/// <summary>Completed I/O bytes. Directions are sampled independently, without a global I/O lock.
/// No packet, endpoint, credential, clipboard or input contents are retained.</summary>
public readonly record struct SessionTrafficSnapshot(SessionTrafficLayer Layer, long ReceivedBytes, long SentBytes);

public interface IRemoteSessionTraffic
{
    SessionTrafficSnapshot Traffic { get; }
}

/// <summary>Thread-safe, bounded-storage counters. The uncontended path is one compare/exchange;
/// saturation prevents wraparound from displaying negative traffic on very long sessions.</summary>
public sealed class SessionTrafficCounter(SessionTrafficLayer layer)
{
    private readonly SessionTrafficLayer _layer = Enum.IsDefined(layer) && layer != SessionTrafficLayer.Unavailable
        ? layer : throw new ArgumentOutOfRangeException(nameof(layer));
    private long _received, _sent;
    public SessionTrafficSnapshot Snapshot => new(_layer, Interlocked.Read(ref _received), Interlocked.Read(ref _sent));
    public void AddReceived(int count) => Add(ref _received, count);
    public void AddSent(int count) => Add(ref _sent, count);
    private static void Add(ref long location, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0) return;
        long old = Interlocked.Read(ref location);
        while (true)
        {
            long next = old > long.MaxValue - count ? long.MaxValue : old + count;
            long observed = Interlocked.CompareExchange(ref location, next, old);
            if (observed == old) return;
            old = observed;
        }
    }
}

public readonly record struct SessionTrafficRates(bool HasInterval, double ReceivedBytesPerSecond, double SentBytesPerSecond);

/// <summary>UI-owner sampler with a caller-supplied monotonic clock. No timer, event history,
/// allocation or network probe. Non-forward clock samples don't consume the baseline.</summary>
public sealed class SessionTrafficSampler
{
    private SessionTrafficSnapshot _previous;
    private long _time;
    private bool _initialized;
    public void Reset() => _initialized = false;
    public SessionTrafficRates Sample(SessionTrafficSnapshot snapshot, long milliseconds)
    {
        if (!Enum.IsDefined(snapshot.Layer) || snapshot.ReceivedBytes < 0 || snapshot.SentBytes < 0)
            throw new ArgumentException("Invalid traffic snapshot.", nameof(snapshot));
        if (snapshot.Layer == SessionTrafficLayer.Unavailable) { Reset(); return default; }
        if (!_initialized || snapshot.Layer != _previous.Layer || snapshot.ReceivedBytes < _previous.ReceivedBytes || snapshot.SentBytes < _previous.SentBytes)
        {
            _previous = snapshot; _time = milliseconds; _initialized = true; return default;
        }
        if (milliseconds <= _time) return default;
        double seconds = unchecked((ulong)milliseconds - (ulong)_time) / 1000d;
        var result = new SessionTrafficRates(true,
            (snapshot.ReceivedBytes - _previous.ReceivedBytes) / seconds,
            (snapshot.SentBytes - _previous.SentBytes) / seconds);
        _previous = snapshot; _time = milliseconds;
        return result;
    }
}
