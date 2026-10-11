using System.Buffers.Binary;
using System.Text;

namespace RemoteSpace.Protocol;

public sealed class RdpProtocolException(string message) : IOException(message);

/// <summary>Bounds-checked, allocation-free reader for untrusted RDP fields.</summary>
public ref struct PacketReader(ReadOnlySpan<byte> data)
{
    private ReadOnlySpan<byte> _data = data;
    public int Remaining => _data.Length;
    public byte U8() { Require(1); byte n = _data[0]; _data = _data[1..]; return n; }
    public ushort U16() { var b = Bytes(2); return BinaryPrimitives.ReadUInt16LittleEndian(b); }
    public ushort U16BE() { var b = Bytes(2); return BinaryPrimitives.ReadUInt16BigEndian(b); }
    public uint U32() { var b = Bytes(4); return BinaryPrimitives.ReadUInt32LittleEndian(b); }
    public int I32() => unchecked((int)U32());
    public ReadOnlySpan<byte> Bytes(int n) { Require(n); var b = _data[..n]; _data = _data[n..]; return b; }
    public void Skip(int n) => Bytes(n);
    public void Require(int n) { if (n < 0 || n > _data.Length) throw new RdpProtocolException($"Truncated packet: need {n} bytes, have {_data.Length}."); }
    public void End() { if (Remaining != 0) throw new RdpProtocolException($"Unexpected trailing data ({Remaining} bytes)."); }
    public int PerLength() { int n = U8(); return (n & 0x80) == 0 ? n : ((n & 0x7f) << 8) | U8(); }
    public ReadOnlySpan<byte> Ber(int expectedTag)
    {
        int tag = U8(); if ((tag & 0x1f) == 0x1f) tag = (tag << 8) | U8();
        if (tag != expectedTag) throw new RdpProtocolException($"Expected BER tag {expectedTag:X}, received {tag:X}.");
        int first = U8(), length = first;
        if ((first & 0x80) != 0)
        {
            int count = first & 0x7f;
            if (count is < 1 or > 3) throw new RdpProtocolException("Indefinite or oversized BER length.");
            length = 0; for (int i = 0; i < count; i++) length = (length << 8) | U8();
        }
        return Bytes(length);
    }
}

public sealed class PacketWriter
{
    private readonly MemoryStream _stream = new();
    public int Length => checked((int)_stream.Length);
    public PacketWriter U8(int n) { _stream.WriteByte(checked((byte)n)); return this; }
    public PacketWriter U16(int n) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, checked((ushort)n)); return Bytes(b); }
    public PacketWriter U16BE(int n) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, checked((ushort)n)); return Bytes(b); }
    public PacketWriter U32(uint n) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, n); return Bytes(b); }
    public PacketWriter Bytes(ReadOnlySpan<byte> bytes) { _stream.Write(bytes); return this; }
    public PacketWriter Zeros(int n) { if (n < 0 || n > 1_048_576) throw new ArgumentOutOfRangeException(nameof(n)); return Bytes(new byte[n]); }
    public PacketWriter Utf16(string value, bool terminate = true) { Bytes(Encoding.Unicode.GetBytes(value)); if (terminate) U16(0); return this; }
    public PacketWriter PerLength(int n)
    {
        if (n is < 0 or > 32767) throw new ArgumentOutOfRangeException(nameof(n));
        return n < 128 ? U8(n) : U16BE(n | 0x8000);
    }
    public PacketWriter Ber(int tag, ReadOnlySpan<byte> value)
    {
        if (tag > 255) U8(tag >> 8); U8(tag & 255);
        if (value.Length < 128) U8(value.Length);
        else if (value.Length <= 255) U8(0x81).U8(value.Length);
        else if (value.Length <= 65535) U8(0x82).U16BE(value.Length);
        else throw new ArgumentOutOfRangeException(nameof(value));
        return Bytes(value);
    }
    public PacketWriter BerInteger(uint value)
    {
        Span<byte> bytes = stackalloc byte[5]; bytes.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(bytes[1..], value);
        int start = 1; while (start < 4 && bytes[start] == 0) start++;
        if ((bytes[start] & 0x80) != 0) start--;
        return Ber(2, bytes[start..]);
    }
    public byte[] ToArray() => _stream.ToArray();
}
