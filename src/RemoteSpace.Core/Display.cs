namespace RemoteSpace.Core;

public sealed record DisplayCapabilities(uint MaximumMonitors, ulong MaximumArea);
public sealed record DisplayMonitor(int Left, int Top, int Width, int Height, bool Primary = true,
    uint PhysicalWidth = 0, uint PhysicalHeight = 0, uint Orientation = 0, uint DesktopScaleFactor = 100, uint DeviceScaleFactor = 100);

/// <summary>Optional session extension. A successful request means sent, not applied:
/// DesktopResized reports the server's actual dimensions after reactivation.</summary>
public interface IRemoteDisplayControl
{
    DisplayCapabilities? DisplayCapabilities { get; }
    event Action<DisplayCapabilities?>? DisplayCapabilitiesChanged;
    ValueTask RequestDisplayLayoutAsync(IReadOnlyList<DisplayMonitor> monitors, CancellationToken cancellationToken = default);
}

public static class DisplayLayout
{
    public const int MaximumMonitors = 16;
    public const ulong MaximumArea = 16_777_216;
    public static void Validate(IReadOnlyList<DisplayMonitor> monitors, DisplayCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(monitors); ArgumentNullException.ThrowIfNull(capabilities);
        if (monitors.Count == 0 || monitors.Count > MaximumMonitors || (uint)monitors.Count > capabilities.MaximumMonitors)
            throw new ArgumentException("The monitor count exceeds the negotiated limit.", nameof(monitors));
        int primary = 0; long left = long.MaxValue, top = long.MaxValue, right = long.MinValue, bottom = long.MinValue;
        foreach (var m in monitors)
        {
            if (m is null || m.Width is < 200 or > 8192 || (m.Width & 1) != 0 || m.Height is < 200 or > 8192 ||
                m.Orientation is not (0 or 90 or 180 or 270) || m.DesktopScaleFactor is < 100 or > 500 || m.DeviceScaleFactor is not (100 or 140 or 180) ||
                !((m.PhysicalWidth == 0 && m.PhysicalHeight == 0) || (m.PhysicalWidth is >= 10 and <= 10000 && m.PhysicalHeight is >= 10 and <= 10000)))
                throw new ArgumentException("Invalid monitor geometry, orientation or scale.", nameof(monitors));
            if (m.Primary) { primary++; if (m.Left != 0 || m.Top != 0) throw new ArgumentException("The primary monitor must start at (0,0).", nameof(monitors)); }
            left = Math.Min(left, m.Left); top = Math.Min(top, m.Top); right = Math.Max(right, (long)m.Left + m.Width); bottom = Math.Max(bottom, (long)m.Top + m.Height);
        }
        if (primary != 1 || right - left > 8192 || bottom - top > 8192 || (ulong)((right - left) * (bottom - top)) > Math.Min(MaximumArea, capabilities.MaximumArea))
            throw new ArgumentException("The desktop layout exceeds its bounds or has an invalid primary monitor.", nameof(monitors));
        for (int i = 0; i < monitors.Count; i++) for (int j = i + 1; j < monitors.Count; j++)
        {
            var a = monitors[i]; var b = monitors[j];
            if (a.Left < (long)b.Left + b.Width && b.Left < (long)a.Left + a.Width && a.Top < (long)b.Top + b.Height && b.Top < (long)a.Top + a.Height)
                throw new ArgumentException("Display monitors must not overlap.", nameof(monitors));
        }
    }
}
