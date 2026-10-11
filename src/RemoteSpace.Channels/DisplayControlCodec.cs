using RemoteSpace.Core;
using RemoteSpace.Protocol;

namespace RemoteSpace.Channels;

/// <summary>MS-RDPEDISP 2.2: little-endian capability and complete monitor layout messages.</summary>
public static class DisplayControlCodec
{
    public const string ChannelName = "Microsoft::Windows::RDS::DisplayControl";
    public static DisplayCapabilities ParseCapabilities(ReadOnlySpan<byte> packet)
    {
        var r = new PacketReader(packet);
        if (r.U32() != 5 || r.U32() != 20 || packet.Length != 20) throw new RdpProtocolException("Invalid display capabilities header.");
        uint count = r.U32(), a = r.U32(), b = r.U32(); r.End();
        if (count == 0 || a == 0 || b == 0) throw new RdpProtocolException("Empty display capability limits.");
        // Saturate the multiplication before applying the local framebuffer allocation bound.
        ulong area = (ulong)a * b;
        area = area > ulong.MaxValue / count ? ulong.MaxValue : area * count;
        return new(Math.Min(count, DisplayLayout.MaximumMonitors), Math.Min(area, DisplayLayout.MaximumArea));
    }
    public static byte[] MonitorLayout(IReadOnlyList<DisplayMonitor> monitors, DisplayCapabilities capabilities)
    {
        DisplayLayout.Validate(monitors, capabilities);
        var w = new PacketWriter().U32(2).U32((uint)(16 + monitors.Count * 40)).U32(40).U32((uint)monitors.Count);
        foreach (var m in monitors) w.U32(m.Primary ? 1u : 0u).U32(unchecked((uint)m.Left)).U32(unchecked((uint)m.Top))
            .U32((uint)m.Width).U32((uint)m.Height).U32(m.PhysicalWidth).U32(m.PhysicalHeight).U32(m.Orientation).U32(m.DesktopScaleFactor).U32(m.DeviceScaleFactor);
        return w.ToArray();
    }
}
