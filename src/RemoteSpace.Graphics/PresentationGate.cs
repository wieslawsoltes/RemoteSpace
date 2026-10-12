namespace RemoteSpace.Graphics;

/// <summary>Thread-safe dirty presentation gate. A nonzero ticket schedules one host
/// callback; requests arriving before it runs coalesce without losing dirty state.
/// Disabling invalidates outstanding tickets and retains the latest dirty state.
/// This controls presentation, not decoding, transport or remote programs.</summary>
public sealed class PresentationGate : IDisposable
{
    private readonly object _gate = new();
    private bool _active, _dirty, _disposed;
    private long _version, _scheduled;
    public long Request()
    {
        lock (_gate)
        {
            if (_disposed) return 0;
            _dirty = true;
            return Schedule();
        }
    }
    public long SetActive(bool active)
    {
        lock (_gate)
        {
            if (_disposed) return 0;
            _active = active;
            if (!active) { _scheduled = 0; return 0; }
            return Schedule();
        }
    }
    public bool IsScheduled(long ticket)
    {
        lock (_gate) return !_disposed && _active && ticket != 0 && _scheduled == ticket;
    }
    public bool Take(long ticket)
    {
        lock (_gate)
        {
            if (_disposed || !_active || ticket == 0 || _scheduled != ticket) return false;
            _scheduled = 0;
            if (!_dirty) return false;
            _dirty = false;
            return true;
        }
    }
    private long Schedule()
    {
        if (!_active || !_dirty || _scheduled != 0) return 0;
        _scheduled = checked(++_version);
        return _scheduled;
    }
    public void Dispose()
    {
        lock (_gate) { _disposed = true; _active = _dirty = false; _scheduled = 0; }
    }
}
