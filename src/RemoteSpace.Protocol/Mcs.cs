using System.Text;
using RemoteSpace.Core;

namespace RemoteSpace.Protocol;

public sealed record ServerSettings(ushort GlobalChannel, ushort[] StaticChannels, uint Version, uint? RequestedProtocols);
public sealed record ChannelData(ushort Initiator, ushort Channel, byte[] Payload);

/// <summary>MS-RDPBCGR 2.2.1.3–2.2.1.9 and T.124/T.125 connection and channel encodings.</summary>
public static class Mcs
{
    public const ushort UserBase = 1001;
    private static byte[] Domain(params uint[] values)
    {
        var w = new PacketWriter(); foreach (uint n in values) w.BerInteger(n);
        return new PacketWriter().Ber(0x30, w.ToArray()).ToArray();
    }
    public static byte[] ConnectInitial(ConnectionOptions options, SecurityProtocols selected)
    {
        options.Validate();
        var core = new PacketWriter().U16(0xc001).U16(216).U32(0x00080004)
            .U16(options.Width).U16(options.Height).U16(0xca01).U16(0xaa03)
            .U32(options.KeyboardLayout).U32(1);
        byte[] name = Encoding.Unicode.GetBytes("RemoteSpace");
        core.Bytes(name).Zeros(32 - name.Length).U32(4).U32(0).U32(12).Zeros(64)
            .U16(0xca01).U16(1).U32(0).U16(options.BitsPerPixel).U16(3).U16(1)
            .Zeros(64).U8(0).U8(0).U32((uint)selected);
        if (core.Length != 216) throw new InvalidOperationException("Client core size invariant.");
        var blocks = new PacketWriter().Bytes(core.ToArray()).U16(0xc002).U16(12).U32(0).U32(0);
        string[] channels = StaticChannelNames(options);
        blocks.U16(0xc003).U16(8 + 12 * channels.Length).U32((uint)channels.Length);
        foreach (string channel in channels)
        {
            byte[] channelName = Encoding.ASCII.GetBytes(channel);
            blocks.Bytes(channelName).Zeros(8 - channelName.Length).U32(0x80200000);
        }
        byte[] data = blocks.ToArray();
        byte[] conference = new PacketWriter().Bytes([0, 8, 0, 16, 0, 1, 0xc0, 0])
            .Bytes("Duca"u8).PerLength(data.Length).Bytes(data).ToArray();
        byte[] gcc = new PacketWriter().Bytes([0, 5, 0, 20, 0x7c, 0, 1]).PerLength(conference.Length).Bytes(conference).ToArray();
        byte[] body = new PacketWriter().Ber(4, [1]).Ber(4, [1]).Ber(1, [255])
            .Bytes(Domain(34, 2, 0, 1, 0, 1, 65535, 2))
            .Bytes(Domain(1, 1, 1, 1, 0, 1, 1056, 2))
            .Bytes(Domain(65535, 64535, 65535, 1, 0, 1, 65535, 2)).Ber(4, gcc).ToArray();
        return new PacketWriter().Ber(0x7f65, body).ToArray();
    }
    public static string[] StaticChannelNames(ConnectionOptions options)
    {
        var names = new List<string>(2);
        if (options.RedirectClipboard) names.Add("cliprdr");
        if (options.EnableDynamicResolution) names.Add("drdynvc");
        return names.ToArray();
    }
    public static ServerSettings ParseConnectResponse(ReadOnlySpan<byte> packet, int expectedChannels)
    {
        var outer = new PacketReader(packet);
        var response = new PacketReader(outer.Ber(0x7f66)); outer.End();
        var result = response.Ber(10);
        if (result.Length != 1 || result[0] != 0) throw new RdpProtocolException("MCS connection rejected.");
        response.Ber(2); response.Ber(0x30);
        var gcc = new PacketReader(response.Ber(4)); response.End();
        if (!gcc.Bytes(7).SequenceEqual(new byte[] { 0, 5, 0, 20, 0x7c, 0, 1 })) throw new RdpProtocolException("Invalid T.124 object identifier.");
        gcc.PerLength(); // MS-RDPBCGR 3.2.5.3.4: this outer length is explicitly ignored.
        if (gcc.U8() != 0x14) throw new RdpProtocolException("Not a GCC conference create response.");
        gcc.U16BE(); int tagLength = gcc.U8();
        if (tagLength is < 1 or > 4) throw new RdpProtocolException("Invalid GCC tag.");
        gcc.Skip(tagLength);
        if (gcc.U8() != 0 || gcc.U8() != 1 || gcc.U8() != 0xc0 || gcc.U8() != 0 || !gcc.Bytes(4).SequenceEqual("McDn"u8))
            throw new RdpProtocolException("Invalid GCC result or H.221 server key.");
        int size = gcc.PerLength(); var blocks = new PacketReader(gcc.Bytes(size)); gcc.End();
        ushort global = 0; ushort[]? channels = null; uint version = 0; uint? requested = null; bool securitySeen = false;
        var seen = new HashSet<ushort>();
        while (blocks.Remaining > 0)
        {
            ushort type = blocks.U16(); int length = blocks.U16();
            if (length < 4 || !seen.Add(type)) throw new RdpProtocolException("Invalid or duplicate server data block.");
            var b = new PacketReader(blocks.Bytes(length - 4));
            switch (type)
            {
                case 0x0c01:
                    version = b.U32(); if (b.Remaining >= 4) requested = b.U32();
                    break;
                case 0x0c02:
                    if (b.U32() != 0 || b.U32() != 0) throw new RdpProtocolException("Legacy security data on a TLS connection.");
                    securitySeen = true; b.End(); break;
                case 0x0c03:
                    global = b.U16(); int count = b.U16();
                    if (global < UserBase || count > 31 || count != expectedChannels) throw new RdpProtocolException("Unexpected MCS channel count or ID.");
                    channels = new ushort[count];
                    for (int i = 0; i < count; i++) channels[i] = b.U16();
                    if ((count & 1) != 0) b.U16();
                    if (channels.Any(c => c < UserBase || c == global) || channels.Distinct().Count() != count) throw new RdpProtocolException("Invalid static channel IDs.");
                    b.End(); break;
                default: break; // Extensible, length-delimited GCC blocks.
            }
        }
        if (version == 0 || !securitySeen || channels is null) throw new RdpProtocolException("Required server settings are missing.");
        return new(global, channels, version, requested);
    }
    public static byte[] ErectDomain() => [4, 1, 0, 1, 0];
    public static byte[] AttachUser() => [0x28];
    public static ushort ParseAttachConfirm(ReadOnlySpan<byte> packet)
    {
        var r = new PacketReader(packet);
        if (r.U8() != 0x2e || r.U8() != 0) throw new RdpProtocolException("MCS user attachment failed.");
        int user = r.U16BE() + UserBase; r.End();
        if (user > ushort.MaxValue) throw new RdpProtocolException("Invalid MCS user ID.");
        return (ushort)user;
    }
    public static byte[] Join(ushort user, ushort channel) => new PacketWriter().U8(0x38).U16BE(user - UserBase).U16BE(channel).ToArray();
    public static void ParseJoinConfirm(ReadOnlySpan<byte> packet, ushort user, ushort channel)
    {
        var r = new PacketReader(packet);
        if (r.U8() != 0x3e || r.U8() != 0 || r.U16BE() + UserBase != user || r.U16BE() != channel || r.U16BE() != channel)
            throw new RdpProtocolException("MCS channel join was rejected or mismatched.");
        r.End();
    }
    public static byte[] SendData(ushort user, ushort channel, ReadOnlySpan<byte> data) => new PacketWriter()
        .U8(0x64).U16BE(user - UserBase).U16BE(channel).U8(0x70).PerLength(data.Length).Bytes(data).ToArray();
    public static ChannelData ParseData(ReadOnlySpan<byte> packet)
    {
        var r = new PacketReader(packet); byte type = r.U8();
        if (type != 0x68) throw new RdpProtocolException($"Unexpected MCS PDU 0x{type:X2}.");
        int initiator = r.U16BE() + UserBase; ushort channel = r.U16BE();
        if (initiator > ushort.MaxValue || r.U8() != 0x70) throw new RdpProtocolException("Unsupported segmented MCS data.");
        int size = r.PerLength(); byte[] payload = r.Bytes(size).ToArray(); r.End();
        return new((ushort)initiator, channel, payload);
    }
}
