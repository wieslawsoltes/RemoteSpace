using System.Buffers.Binary;

namespace RemoteSpace.Protocol;

public sealed record WirePacket(bool IsFastPath, byte Header, byte[] Payload);

/// <summary>TPKT (RFC 1006), X.224 class 0, and RDP fast-path framing. No socket assumptions.</summary>
public static class Framing
{
    public const int MaximumPacket = 65535;
    public static byte[] Tpkt(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumPacket - 4) throw new ArgumentOutOfRangeException(nameof(payload));
        byte[] packet = new byte[payload.Length + 4]; packet[0] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        payload.CopyTo(packet.AsSpan(4)); return packet;
    }
    public static byte[] Data(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumPacket - 7) throw new ArgumentOutOfRangeException(nameof(payload));
        byte[] packet = new byte[payload.Length + 7]; packet[0] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        packet[4] = 2; packet[5] = 0xf0; packet[6] = 0x80;
        payload.CopyTo(packet.AsSpan(7)); return packet;
    }
    public static byte[] UnwrapData(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        if (r.U8() != 2 || r.U8() != 0xf0 || r.U8() != 0x80) throw new RdpProtocolException("Invalid X.224 data header.");
        return r.Bytes(r.Remaining).ToArray();
    }
    public static async ValueTask<WirePacket> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] h = new byte[4];
        await stream.ReadExactlyAsync(h.AsMemory(0, 2), ct);
        if (h[0] == 3)
        {
            if (h[1] != 0) throw new RdpProtocolException("Invalid TPKT reserved byte.");
            await stream.ReadExactlyAsync(h.AsMemory(2, 2), ct);
            int length = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(2));
            if (length < 7) throw new RdpProtocolException("TPKT length is below the minimum.");
            byte[] data = new byte[length - 4];
            await stream.ReadExactlyAsync(data, ct);
            return new(false, 0, data);
        }
        if ((h[0] & 3) != 0 || (h[0] & 0xc0) != 0) throw new RdpProtocolException("Unsupported fast-path action or standard-security encryption.");
        int size = h[1], prefix = 2;
        if ((size & 0x80) != 0)
        {
            await stream.ReadExactlyAsync(h.AsMemory(2, 1), ct);
            size = ((size & 0x7f) << 8) | h[2]; prefix = 3;
        }
        if (size < prefix) throw new RdpProtocolException("Invalid fast-path length.");
        byte[] body = new byte[size - prefix];
        await stream.ReadExactlyAsync(body, ct);
        return new(true, h[0], body);
    }
}

[Flags] public enum SecurityProtocols : uint { Standard = 0, Tls = 1, Hybrid = 2, HybridExtended = 8 }
public sealed record NegotiationResult(SecurityProtocols Protocol, byte Flags);

public static class Negotiation
{
    // MS-RDPBCGR 2.2.1.1: no routing cookie is needed for direct PC connections.
    public static byte[] Request(SecurityProtocols requested) => Framing.Tpkt(new PacketWriter()
        .U8(14).U8(0xe0).U16(0).U16(0).U8(0).U8(1).U8(0).U16(8).U32((uint)requested).ToArray());

    public static NegotiationResult ParseConfirm(ReadOnlySpan<byte> payload, SecurityProtocols requested)
    {
        var r = new PacketReader(payload);
        int length = r.U8();
        if (length != payload.Length - 1 || r.U8() != 0xd0) throw new RdpProtocolException("Invalid X.224 connection confirm.");
        r.Skip(5);
        if (r.Remaining == 0) throw new RdpProtocolException("Server selected legacy RDP security; a TLS connection is required.");
        byte type = r.U8(), flags = r.U8();
        if (r.U16() != 8) throw new RdpProtocolException("Invalid RDP negotiation length.");
        uint value = r.U32(); r.End();
        if (type == 3) throw new RdpProtocolException($"Server rejected security negotiation (RDP_NEG_FAILURE 0x{value:X8}).");
        if (type != 2 || value is not (1 or 2 or 8) || (value & (uint)requested) == 0)
            throw new RdpProtocolException("Server selected an unrequested or unsupported security protocol.");
        return new((SecurityProtocols)value, flags);
    }
}
