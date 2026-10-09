using System.Text;

namespace RemoteSpace.Protocol;

/// <summary>Per-channel bounded static virtual-channel reassembly (MS-RDPBCGR 2.2.6.1).</summary>
public sealed class ChannelAssembler(int maximumLength = 4 * 1024 * 1024)
{
    private MemoryStream? _message;
    private int _expected;
    public byte[]? Accept(ReadOnlySpan<byte> chunk)
    {
        var r = new PacketReader(chunk); uint length = r.U32(), flags = r.U32();
        if (length > maximumLength || (flags & ~0x13U) != 0) throw new RdpProtocolException("Unsupported channel flags or oversized message.");
        if ((flags & 1) != 0)
        {
            if (_message is not null) throw new RdpProtocolException("Nested first channel fragment.");
            _expected = (int)length; _message = new MemoryStream(Math.Min(_expected, 65536));
        }
        if (_message is null || length != _expected || _message.Length + r.Remaining > _expected)
            throw new RdpProtocolException("Out-of-order or overflowing channel fragment.");
        _message.Write(r.Bytes(r.Remaining));
        if ((flags & 2) == 0) return null;
        if (_message.Length != _expected) throw new RdpProtocolException("Incomplete last channel fragment.");
        byte[] result = _message.ToArray(); _message.Dispose(); _message = null; return result;
    }
    public static IEnumerable<byte[]> Fragment(byte[] payload, int chunkSize = 1600)
    {
        if (payload.Length > 4 * 1024 * 1024 || chunkSize is < 1 or > 1600) throw new ArgumentOutOfRangeException(nameof(payload));
        for (int offset = 0; offset < Math.Max(payload.Length, 1); offset += chunkSize)
        {
            int count = Math.Min(chunkSize, payload.Length - offset);
            uint flags = (offset == 0 ? 1U : 0) | (offset + count == payload.Length ? 2U : 0);
            yield return new PacketWriter().U32((uint)payload.Length).U32(flags).Bytes(payload.AsSpan(offset, count)).ToArray();
        }
    }
}

public sealed record ClipboardPacket(ushort Type, ushort Flags, byte[] Data);

public static class ClipboardCodec
{
    public const int MaximumTextBytes = 1024 * 1024;
    public static ClipboardPacket Parse(ReadOnlySpan<byte> bytes)
    {
        var r = new PacketReader(bytes); ushort type = r.U16(), flags = r.U16(); uint length = r.U32();
        if (length > MaximumTextBytes + 1024 || length != r.Remaining) throw new RdpProtocolException("Invalid clipboard PDU length.");
        return new(type, flags, r.Bytes((int)length).ToArray());
    }
    public static byte[] Pdu(ushort type, ushort flags, ReadOnlySpan<byte> payload) => new PacketWriter().U16(type).U16(flags).U32((uint)payload.Length).Bytes(payload).ToArray();
    public static byte[] Capabilities() => Pdu(7, 0, new PacketWriter().U16(1).U16(0).U16(1).U16(12).U32(2).U32(2).ToArray());
    public static byte[] FormatList(bool longNames) => Pdu(2, 0, new PacketWriter().U32(13).Zeros(longNames ? 2 : 32).ToArray());
    public static bool HasUnicodeText(ReadOnlySpan<byte> data, bool longNames)
    {
        var r = new PacketReader(data); bool found = false; int count = 0;
        while (r.Remaining > 0)
        {
            if (++count > 4096) throw new RdpProtocolException("Too many clipboard formats.");
            found |= r.U32() == 13;
            if (longNames) { int chars = 0; while (r.U16() != 0) if (++chars > 1024) throw new RdpProtocolException("Clipboard format name is too long."); }
            else r.Skip(32);
        }
        return found;
    }
    public static bool SupportsLongNames(ReadOnlySpan<byte> data)
    {
        var r = new PacketReader(data); int count = r.U16(); r.U16(); bool enabled = false;
        if (count > 32) throw new RdpProtocolException("Too many clipboard capabilities.");
        for (int i = 0; i < count; i++)
        {
            int type = r.U16(), size = r.U16(); if (size < 4) throw new RdpProtocolException("Invalid clipboard capability size.");
            var c = new PacketReader(r.Bytes(size - 4));
            if (type == 1) { c.U32(); enabled = (c.U32() & 2) != 0; }
        }
        r.End(); return enabled;
    }
    public static byte[] TextResponse(string text)
    {
        if (text.Contains('\0') || Encoding.Unicode.GetByteCount(text) + 2 > MaximumTextBytes) throw new ArgumentException("Clipboard text contains NUL or exceeds one megabyte.");
        return Pdu(5, 1, Encoding.Unicode.GetBytes(text + "\0"));
    }
    public static string ReadText(ReadOnlySpan<byte> text)
    {
        if (text.Length is < 2 or > MaximumTextBytes || (text.Length & 1) != 0 || text[^1] != 0 || text[^2] != 0)
            throw new RdpProtocolException("Invalid Unicode clipboard text.");
        string value = new UnicodeEncoding(false, false, true).GetString(text[..^2]);
        if (value.Contains('\0')) throw new RdpProtocolException("Embedded clipboard NUL.");
        return value;
    }
}

public sealed class FastPathAssembler
{
    private MemoryStream? _parts;
    private byte _code;
    public void Accept(ReadOnlySpan<byte> bytes, Action<byte, byte[]> completed)
    {
        var r = new PacketReader(bytes);
        while (r.Remaining > 0)
        {
            byte header = r.U8(), code = (byte)(header & 15); int fragment = (header >> 4) & 3;
            if ((header & 0xc0) == 0x80) { if ((r.U8() & 0x20) != 0) throw new RdpProtocolException("Unnegotiated fast-path bulk compression."); }
            else if ((header & 0xc0) != 0) throw new RdpProtocolException("Invalid fast-path compression flags.");
            int length = r.U16(); var data = r.Bytes(length);
            if (fragment == 0)
            {
                if (_parts is not null) throw new RdpProtocolException("Single update interrupted a fragmented update.");
                completed(code, data.ToArray()); continue;
            }
            if (fragment == 2)
            {
                if (_parts is not null) throw new RdpProtocolException("Nested fast-path fragment.");
                _parts = new MemoryStream(); _code = code;
            }
            if (_parts is null || _code != code || _parts.Length + length > 16 * 1024 * 1024)
                throw new RdpProtocolException("Invalid fast-path fragment sequence or size.");
            _parts.Write(data);
            if (fragment == 1)
            {
                byte[] assembled = _parts.ToArray(); _parts.Dispose(); _parts = null; completed(code, assembled);
            }
        }
    }
}
