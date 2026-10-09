using System.Text;
using RemoteSpace.Core;

namespace RemoteSpace.Protocol;

public sealed record SharePacket(byte Type, ushort Source, byte[] Body);
public sealed record ShareDataPacket(uint ShareId, byte Type, byte[] Body);
public sealed record DemandActive(uint ShareId, int Width, int Height, int BitsPerPixel);

public static class Activation
{
    public const uint ClientInfoFlags = 0x004b017b;

    public static byte[] ClientInfo(ConnectionOptions options, SessionCredentials credentials, bool nla)
    {
        string[] fields = [credentials.Domain, credentials.UserName, nla ? "" : credentials.Password, "", ""];
        if (fields.Any(s => s.Length > 255 || s.Contains('\0'))) throw new ArgumentException("RDP credential fields must be at most 255 characters and cannot contain NUL.");
        // Includes INFO_MAXIMIZESHELL: required by xrdp's INFO_LOGON_NORMAL validation.
        var w = new PacketWriter().U16(0x40).U16(0).U32(options.KeyboardLayout & 0xffff).U32(ClientInfoFlags);
        foreach (string value in fields) w.U16(Encoding.Unicode.GetByteCount(value));
        foreach (string value in fields) w.Utf16(value);
        const string address = "0.0.0.0", directory = "C:\\RemoteSpace";
        w.U16(2).U16((address.Length + 1) * 2).Utf16(address).U16((directory.Length + 1) * 2).Utf16(directory)
            .Zeros(172).U32(0).U32(0).U16(0).U16(0).U16(0);
        return w.ToArray();
    }
    public static IEnumerable<SharePacket> ParseShares(byte[] data)
    {
        var result = new List<SharePacket>(); int offset = 0;
        while (offset < data.Length)
        {
            var r = new PacketReader(data.AsSpan(offset)); int length = r.U16();
            if (length < 6 || length > data.Length - offset) throw new RdpProtocolException($"Invalid share control length {length} in a {data.Length - offset}-byte envelope.");
            ushort type = r.U16(), source = r.U16();
            if ((type & 0x00f0) != 0x10) throw new RdpProtocolException($"Invalid share control version: type=0x{type:X4}, length={length}, source={source}.");
            result.Add(new((byte)(type & 15), source, r.Bytes(length - 6).ToArray())); offset += length;
        }
        return result;
    }
    public static ShareDataPacket ParseData(ReadOnlySpan<byte> body)
    {
        var r = new PacketReader(body); uint share = r.U32(); r.U8(); r.U8();
        int uncompressed = r.U16(); byte type = r.U8(), compression = r.U8(); ushort compressedLength = r.U16();
        if ((compression & 0x20) != 0) throw new RdpProtocolException("Server sent bulk-compressed data that was not negotiated.");
        // MS-RDPBCGR 4.1.14: excludes the first 14 bytes of the full PDU.
        if (uncompressed != 0 && uncompressed != body.Length - 8) throw new RdpProtocolException("Invalid uncompressed share data length.");
        if (compressedLength != 0 && compression == 0) throw new RdpProtocolException("Unexpected compressed data length.");
        return new(share, type, r.Bytes(r.Remaining).ToArray());
    }
    public static DemandActive ParseDemand(ReadOnlySpan<byte> body)
    {
        var r = new PacketReader(body); uint share = r.U32(); int sourceSize = r.U16(), capSize = r.U16();
        r.Skip(sourceSize); var caps = new PacketReader(r.Bytes(capSize)); int count = caps.U16(); caps.U16();
        if (count > 128) throw new RdpProtocolException("Too many capability sets.");
        int width = 0, height = 0, bpp = 0;
        var types = new HashSet<ushort>();
        for (int i = 0; i < count; i++)
        {
            ushort type = caps.U16(); int length = caps.U16();
            if (length < 4 || !types.Add(type)) throw new RdpProtocolException("Invalid or duplicate capability.");
            var c = new PacketReader(caps.Bytes(length - 4));
            if (type == 2) { bpp = c.U16(); c.Skip(6); width = c.U16(); height = c.U16(); }
        }
        caps.End();
        if (r.Remaining == 4) r.U32(); r.End();
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || (long)width * height > 16_777_216 || bpp is not (8 or 15 or 16 or 24))
            throw new RdpProtocolException("Unsupported server desktop dimensions or pixel format.");
        return new(share, width, height, bpp);
    }
    private static byte[] Capability(int type, PacketWriter body) => new PacketWriter().U16(type).U16(body.Length + 4).Bytes(body.ToArray()).ToArray();
    public static byte[] Confirm(DemandActive demand, ushort user, uint keyboardLayout)
    {
        var sets = new List<byte[]>
        {
            Capability(1, new PacketWriter().U16(1).U16(3).U16(0x200).U16(0).U16(0).U16(0x401).U16(0).U16(0).U16(0).U8(1).U8(1)),
            Capability(2, new PacketWriter().U16(demand.BitsPerPixel).U16(1).U16(1).U16(1).U16(demand.Width).U16(demand.Height)
                .U16(0).U16(1).U16(1).U8(0).U8(0).U16(1).U16(0)),
            Capability(3, new PacketWriter().Zeros(16).U32(0).U16(1).U16(20).U16(0).U16(1).U16(0).U16(2).Zeros(32)
                .U16(0).U16(0).U32(0).U32(0).U16(0).U16(0).U16(1252).U16(0)),
            Capability(4, new PacketWriter().Zeros(36)),
            Capability(5, new PacketWriter().U16(0).U16(0).U16(2).U16(2)),
            Capability(7, new PacketWriter().Zeros(8)),
            Capability(8, new PacketWriter().U16(1).U16(20).U16(20)),
            Capability(9, new PacketWriter().U16(0).U16(0)),
            Capability(10, new PacketWriter().U16(6).U16(0)),
            Capability(12, new PacketWriter().U16(0).U16(0)),
            Capability(13, new PacketWriter().U16(0x11).U16(0).U32(keyboardLayout).U32(4).U32(0).U32(12).Zeros(64)),
            Capability(14, new PacketWriter().U16(1).U16(0)),
            Capability(16, new PacketWriter().Zeros(48)),
            Capability(20, new PacketWriter().U32(0).U32(1600)),
            Capability(26, new PacketWriter().U32(16 * 1024 * 1024))
        };
        var cap = new PacketWriter().U16(sets.Count).U16(0); foreach (byte[] set in sets) cap.Bytes(set);
        byte[] descriptor = "RemoteSpace\0"u8.ToArray();
        byte[] body = new PacketWriter().U32(demand.ShareId).U16(1002).U16(descriptor.Length).U16(cap.Length).Bytes(descriptor).Bytes(cap.ToArray()).ToArray();
        return Share(3, user, body);
    }
    public static byte[] Share(byte type, ushort user, ReadOnlySpan<byte> body) => new PacketWriter().U16(body.Length + 6).U16(type | 0x10).U16(user).Bytes(body).ToArray();
    public static byte[] Data(byte type, uint share, ushort user, ReadOnlySpan<byte> body) => Share(7, user, new PacketWriter()
        .U32(share).U8(0).U8(1).U16(body.Length + 4).U8(type).U8(0).U16(0).Bytes(body).ToArray());
    public static byte[] Synchronize(uint share, ushort user, ushort server) => Data(31, share, user, new PacketWriter().U16(1).U16(server).ToArray());
    public static byte[] Control(uint share, ushort user, ushort action) => Data(20, share, user, new PacketWriter().U16(action).U16(0).U32(0).ToArray());
    public static byte[] FontList(uint share, ushort user) => Data(39, share, user, new PacketWriter().U16(0).U16(0).U16(3).U16(50).ToArray());
    public static byte[] Input(uint share, ushort user, RemoteInput input)
    {
        var w = new PacketWriter().U16(1).U16(0).U32(0);
        switch (input.Kind)
        {
            case InputKind.ScanCode: w.U16(4).U16(input.Flags).U16(input.Code).U16(0); break;
            case InputKind.Unicode: w.U16(5).U16(input.Flags).U16(input.Code).U16(0); break;
            case InputKind.Pointer: w.U16(0x8001).U16(input.Flags).U16(input.X).U16(input.Y); break;
            case InputKind.Synchronize: w.U16(0).U16(0).U32(input.Code); break;
            default: throw new ArgumentOutOfRangeException(nameof(input));
        }
        return Data(28, share, user, w.ToArray());
    }
    public static byte[] Refresh(uint share, ushort user, int width, int height) => Data(33, share, user,
        new PacketWriter().U8(1).Zeros(3).U16(0).U16(0).U16(width - 1).U16(height - 1).ToArray());
}
