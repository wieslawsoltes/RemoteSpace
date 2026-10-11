using System.Text;
using RemoteSpace.Protocol;

namespace RemoteSpace.Channels;

public enum DynamicChannelEventKind { Opened, Data, Closed }
public sealed record DynamicChannelEvent(DynamicChannelEventKind Kind, uint Id, string Name, byte[] Data);
public sealed record DynamicChannelResult(byte[][] Replies, DynamicChannelEvent? Event = null);

/// <summary>Independent MS-RDPEDYC client-side v1 multiplexer. Accept receives one reassembled
/// drdynvc static-channel message. Call on one receive thread; serialize complete outbound messages.
/// v2/v3 offers select v1: priorities, compression and multitransport are not falsely advertised.</summary>
public sealed class DynamicChannels
{
    public const int MaximumMessage = 4 * 1024 * 1024;
    public const int MaximumChannels = 64;
    private readonly HashSet<string> _listeners;
    private readonly Dictionary<uint, Channel> _channels = new();
    private int _allocated;
    public int OpenChannelCount => _channels.Count;
    public int BufferedBytes => _allocated;
    public ushort NegotiatedVersion { get; private set; }
    public DynamicChannels(IEnumerable<string> listeners)
    {
        ArgumentNullException.ThrowIfNull(listeners); _listeners = new(listeners, StringComparer.Ordinal);
        if (_listeners.Count > MaximumChannels || _listeners.Any(n => string.IsNullOrEmpty(n) || n.Length > 255 || n.Any(c => c < 32 || c > 126)))
            throw new ArgumentException("Register at most 64 printable ASCII channel names.", nameof(listeners));
    }
    public DynamicChannelResult Accept(ReadOnlySpan<byte> packet)
    {
        try { return Process(packet); }
        catch { Reset(); throw; } // A malformed peer never retains outstanding allocations.
    }
    public void Reset() { _channels.Clear(); _allocated = 0; NegotiatedVersion = 0; }
    private DynamicChannelResult Process(ReadOnlySpan<byte> packet)
    {
        if (packet.Length > 1600) throw new RdpProtocolException("Oversized dynamic-channel PDU.");
        var r = new PacketReader(packet); byte header = r.U8(); int command = header >> 4, size = header & 3;
        if (command == 5)
        {
            if (size != 0 || r.U8() != 0 || NegotiatedVersion != 0) throw new RdpProtocolException("Invalid or duplicate DVC capabilities.");
            ushort offered = r.U16();
            if (offered is < 1 or > 3) throw new RdpProtocolException("Unsupported DVC capability version.");
            if (offered >= 2) r.Skip(8); r.End(); NegotiatedVersion = 1;
            return new([[0x50, 0, 1, 0]]);
        }
        if (NegotiatedVersion == 0) throw new RdpProtocolException("DVC operation before capability negotiation.");
        uint id = ReadVariable(ref r, size);
        if (command == 1)
        {
            var bytes = r.Bytes(r.Remaining);
            if (bytes.Length is < 2 or > 256 || bytes[^1] != 0 || bytes[..^1].Contains((byte)0)) throw new RdpProtocolException("Invalid DVC listener name.");
            string name = Encoding.Latin1.GetString(bytes[..^1]);
            if (_channels.ContainsKey(id)) throw new RdpProtocolException("Duplicate DVC channel ID.");
            bool accepted = _listeners.Contains(name) && _channels.Count < MaximumChannels;
            // Echo cbId, not merely the shortest ID encoding, for interoperability.
            byte[] reply = Variable(new PacketWriter().U8(0x10 | size), id, size).U32(accepted ? 0u : 0xc0000001u).ToArray();
            if (!accepted) return new([reply]);
            _channels.Add(id, new(name));
            return new([reply], new(DynamicChannelEventKind.Opened, id, name, []));
        }
        if (command == 4)
        {
            r.End();
            if (!_channels.Remove(id, out var closed)) return new([]);
            _allocated -= closed.Buffer?.Length ?? 0;
            return new([Variable(new PacketWriter().U8(0x40 | size), id, size).ToArray()], new(DynamicChannelEventKind.Closed, id, closed.Name, []));
        }
        if (command is not (2 or 3)) throw new RdpProtocolException("Unnegotiated compressed or unknown DVC command.");
        if (!_channels.TryGetValue(id, out var channel)) throw new RdpProtocolException("Data on an unopened DVC.");
        if (command == 2)
        {
            uint length = ReadVariable(ref r, (header >> 2) & 3);
            if (channel.Buffer is not null || length == 0 || length > MaximumMessage - _allocated || r.Remaining > length)
                throw new RdpProtocolException("Invalid or excessive fragmented DVC message.");
            channel.Buffer = new byte[length]; _allocated += (int)length; channel.Written = 0;
        }
        var data = r.Bytes(r.Remaining);
        if (channel.Buffer is null) return new([], new(DynamicChannelEventKind.Data, id, channel.Name, data.ToArray()));
        if (data.Length == 0 || data.Length > channel.Buffer.Length - channel.Written) throw new RdpProtocolException("Invalid DVC fragment continuation.");
        data.CopyTo(channel.Buffer.AsSpan(channel.Written)); channel.Written += data.Length;
        if (channel.Written != channel.Buffer.Length) return new([]);
        byte[] complete = channel.Buffer; _allocated -= complete.Length; channel.Buffer = null; channel.Written = 0;
        return new([], new(DynamicChannelEventKind.Data, id, channel.Name, complete));
    }
    /// <summary>Packetize an application message without including static-channel headers.</summary>
    public static byte[][] Fragment(uint id, ReadOnlySpan<byte> message)
    {
        if (message.Length > MaximumMessage) throw new ArgumentOutOfRangeException(nameof(message));
        int size = WidthCode(id); var output = new List<byte[]>(); int offset = 0;
        if (message.Length <= 1590) return [Variable(new PacketWriter().U8(0x30 | size), id, size).Bytes(message).ToArray()];
        int lenSize = WidthCode((uint)message.Length);
        var first = Variable(Variable(new PacketWriter().U8(0x20 | (lenSize << 2) | size), id, size), (uint)message.Length, lenSize);
        int take = Math.Min(message.Length, 1600 - first.Length);
        output.Add(first.Bytes(message[..take]).ToArray()); offset += take;
        while (offset < message.Length)
        {
            var next = Variable(new PacketWriter().U8(0x30 | size), id, size);
            take = Math.Min(message.Length - offset, 1600 - next.Length);
            output.Add(next.Bytes(message.Slice(offset, take)).ToArray()); offset += take;
        }
        return output.ToArray();
    }
    private static int WidthCode(uint value) => value <= byte.MaxValue ? 0 : value <= ushort.MaxValue ? 1 : 2;
    private static uint ReadVariable(ref PacketReader reader, int size) => size switch { 0 => reader.U8(), 1 => reader.U16(), 2 => reader.U32(), _ => throw new RdpProtocolException("Reserved DVC field width.") };
    private static PacketWriter Variable(PacketWriter writer, uint value, int size) => size switch { 0 => writer.U8(checked((byte)value)), 1 => writer.U16(checked((ushort)value)), 2 => writer.U32(value), _ => throw new ArgumentOutOfRangeException(nameof(size)) };
    private sealed class Channel(string name)
    {
        public string Name { get; } = name;
        public byte[]? Buffer { get; set; }
        public int Written { get; set; }
    }
}
