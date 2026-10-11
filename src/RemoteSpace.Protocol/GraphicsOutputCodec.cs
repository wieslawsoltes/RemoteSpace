using RemoteSpace.Core;

namespace RemoteSpace.Protocol;

/// <summary>MS-RDPBCGR 2.2.7.1.1 and 2.2.11.3.1. Server capabilities must be
/// checked before sending refresh or suppression. Rectangle bounds are inclusive.</summary>
public static class GraphicsOutputCodec
{
    public static GraphicsOutputCapabilities ReadCapabilities(ReadOnlySpan<byte> demand)
    {
        var r = new PacketReader(demand); r.U32(); int source = r.U16(), length = r.U16();
        r.Skip(source); var caps = new PacketReader(r.Bytes(length)); int count = caps.U16(); caps.U16();
        if (count > 128) throw new RdpProtocolException("Too many capabilities.");
        bool found = false, refresh = false, suppress = false;
        for (int i = 0; i < count; i++)
        {
            int type = caps.U16(), size = caps.U16();
            if (size < 4) throw new RdpProtocolException("Invalid capability length.");
            var body = new PacketReader(caps.Bytes(size - 4));
            if (type != 1) continue;
            if (found) throw new RdpProtocolException("Duplicate general capabilities.");
            found = true; body.Skip(18); refresh = body.U8() != 0; suppress = body.U8() != 0;
        }
        caps.End(); if (r.Remaining == 4) r.U32(); r.End();
        return new(refresh, suppress);
    }
    public static byte[] Suppress(uint share, ushort user, bool enabled, int width, int height)
    {
        ValidateSize(width, height);
        var body = new PacketWriter().U8(enabled ? 1 : 0).Zeros(3);
        if (enabled) body.U16(0).U16(0).U16(width - 1).U16(height - 1);
        return Activation.Data(35, share, user, body.ToArray());
    }
    public static byte[] Refresh(uint share, ushort user, int width, int height)
    {
        ValidateSize(width, height); return Activation.Refresh(share, user, width, height);
    }
    private static void ValidateSize(int width, int height)
    {
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || (long)width * height > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(width), "Desktop rectangle exceeds its allocation bounds.");
    }
}
