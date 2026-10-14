namespace RemoteSpace.Core;

/// <summary>One remote pixel per local layout unit. Server DPI and device-pixel
/// scaling are deliberately separate. Odd widths round down by one pixel.</summary>
public readonly record struct DisplayResizeTarget(int Width, int Height)
{
    public static bool TryFromViewport(double width, double height, DisplayCapabilities? capabilities,
        out DisplayResizeTarget target)
    {
        target = default;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width is < 200 or > 8192 ||
            height is < 200 or > 8192 || capabilities is not { MaximumMonitors: > 0 }) return false;
        int w = (int)width & ~1, h = (int)height;
        if ((ulong)w * (ulong)h > Math.Min(DisplayLayout.MaximumArea, capabilities.MaximumArea)) return false;
        target = new(w, h);
        return true;
    }
}

public readonly record struct DisplayResizeRequest(long Ticket, DisplayResizeTarget Target);

/// <summary>Single-owner, deterministic automatic-resize planner. No threads,
/// timers, tasks or per-offer allocation. At most one in-flight request and one
/// latest target are retained. The host supplies monotonic milliseconds and must
/// complete a request only after server confirmation (not merely a socket send).</summary>
public sealed class DisplayResizeQueue
{
    public const int SettleMilliseconds = 250;
    private DisplayResizeTarget? _target;
    private long _due, _nextTicket, _inFlight;
    private bool _available;
    public bool Enabled { get; private set; }
    public bool IsBusy => _inFlight != 0;
    public long StartedRequests { get; private set; }

    public void SetEnabled(bool enabled, long now)
    {
        if (Enabled == enabled) return;
        Enabled = enabled;
        _due = now + SettleMilliseconds;
        // An already sent request cannot be undone. Keep tracking it so a
        // disable/re-enable cannot start a concurrent server reactivation.
    }

    public void SetAvailable(bool available, long now)
    {
        if (_available == available) return;
        _available = available;
        if (available) _due = now + SettleMilliseconds;
    }

    public bool Offer(double width, double height, DisplayCapabilities? capabilities, long now)
    {
        DisplayResizeTarget? target = DisplayResizeTarget.TryFromViewport(width, height, capabilities, out var value) ? value : null;
        if (_target == target) return false;
        _target = target;
        _due = now + SettleMilliseconds;
        return true;
    }

    public int? DelayMilliseconds(long now, int actualWidth, int actualHeight)
    {
        if (!Enabled || !_available || IsBusy || _target is not { } target ||
            (target.Width == actualWidth && target.Height == actualHeight)) return null;
        return (int)Math.Clamp(_due - now, 1, SettleMilliseconds);
    }

    public bool TryBegin(long now, int actualWidth, int actualHeight, out DisplayResizeRequest request)
    {
        request = default;
        if (DelayMilliseconds(now, actualWidth, actualHeight) is null || now < _due) return false;
        _inFlight = ++_nextTicket;
        StartedRequests++;
        request = new(_inFlight, _target!.Value);
        return true;
    }

    public bool Complete(long ticket, bool confirmed, long now)
    {
        if (_inFlight == 0 || ticket != _inFlight) return false;
        _inFlight = 0;
        if (!confirmed) Enabled = false; // Do not retry an unconfirmed layout indefinitely.
        _due = Math.Max(_due, now + SettleMilliseconds);
        return true;
    }
}
