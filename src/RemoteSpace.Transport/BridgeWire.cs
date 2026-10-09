using System.Buffers;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteSpace.Core;

namespace RemoteSpace.Transport;

// Deliberately a class: record-generated ToString must not expose connection credentials.
public sealed class BridgeControl
{
    public string Type { get; set; } = "";
    public string? Token { get; set; }
    public ConnectionOptions? Options { get; set; }
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? Domain { get; set; }
    public SessionState State { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Stage { get; set; }
    public string? Message { get; set; }
    public string? Text { get; set; }
    public RemoteInput Input { get; set; }
    public RemoteCursor? Cursor { get; set; }
}
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BridgeControl))]
public partial class BridgeJsonContext : JsonSerializerContext { }

public sealed record BridgePacket(bool Binary, byte[] Payload);

/// <summary>Versioned browser transport for the same RDP engine. This framing is not the RDP wire protocol.</summary>
public static class BridgeWire
{
    public const string SubProtocol = "remotespace.v1";
    public const int MaximumBinary = 4 * 1024 * 1024 + 24;
    public const int MaximumControl = 2 * 1024 * 1024;
    public static byte[] Control(BridgeControl message) => JsonSerializer.SerializeToUtf8Bytes(message, BridgeJsonContext.Default.BridgeControl);
    public static BridgeControl ParseControl(byte[] data) => JsonSerializer.Deserialize(data, BridgeJsonContext.Default.BridgeControl) ?? throw new InvalidDataException("Empty control message.");
    public static byte[] Surface(SurfaceUpdate update)
    {
        if (update.X < 0 || update.Y < 0 || update.Width < 1 || update.Height < 1 || (long)update.X + update.Width > 8192 || (long)update.Y + update.Height > 8192 || update.Rgba.LongLength != (long)update.Width * update.Height * 4 || update.Rgba.Length > MaximumBinary - 24)
            throw new InvalidDataException("Invalid or oversized browser surface rectangle.");
        byte[] result = new byte[24 + update.Rgba.Length]; "RSU1"u8.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), update.X); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), update.Y);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), update.Width); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16), update.Height);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(20), update.Rgba.Length); update.Rgba.CopyTo(result, 24); return result;
    }
    public static SurfaceUpdate ParseSurface(byte[] data)
    {
        if (data.Length < 24 || data.Length > MaximumBinary || !data.AsSpan(0, 4).SequenceEqual("RSU1"u8)) throw new InvalidDataException("Invalid browser surface header.");
        int x = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4)), y = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        int width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12)), height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16)), length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(20));
        if (x < 0 || y < 0 || width < 1 || height < 1 || (long)x + width > 8192 || (long)y + height > 8192 || length != data.Length - 24 || (long)width * height * 4 != length)
            throw new InvalidDataException("Browser surface dimensions or length mismatch.");
        return new(x, y, width, height, data.AsSpan(24).ToArray());
    }
    public static IEnumerable<SurfaceUpdate> Split(SurfaceUpdate update)
    {
        int rows = Math.Max(1, Math.Min(64, (MaximumBinary - 24) / (update.Width * 4)));
        for (int y = 0; y < update.Height; y += rows)
        {
            int count = Math.Min(rows, update.Height - y);
            yield return new(update.X, update.Y + y, update.Width, count, update.Rgba.AsSpan(y * update.Width * 4, count * update.Width * 4).ToArray());
        }
    }
    public static async ValueTask<BridgePacket?> ReceiveAsync(WebSocket socket, CancellationToken ct, int controlLimit = MaximumControl)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            using var data = new MemoryStream(); WebSocketMessageType? type = null;
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (received.MessageType == WebSocketMessageType.Close) return null;
                type ??= received.MessageType;
                if (type != received.MessageType) throw new InvalidDataException("WebSocket fragment type changed.");
                int limit = type == WebSocketMessageType.Binary ? MaximumBinary : controlLimit;
                if (data.Length + received.Count > limit) throw new InvalidDataException("WebSocket message exceeds the configured limit.");
                data.Write(buffer, 0, received.Count);
                if (received.EndOfMessage) return new(type == WebSocketMessageType.Binary, data.ToArray());
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
