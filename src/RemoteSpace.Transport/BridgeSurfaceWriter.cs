using System.Buffers;
using System.Buffers.Binary;
using System.Net.WebSockets;
using RemoteSpace.Core;

namespace RemoteSpace.Transport;

/// <summary>Single-owner sender for the existing RSU1 wire format. One reusable pooled
/// packet replaces per-strip pixel and packet allocations. Do not dispose or call
/// concurrently with SendAsync; the caller owns the socket and immutable snapshots.</summary>
public sealed class BridgeSurfaceWriter : IDisposable
{
    public const int MaximumPacketBytes = 1024 * 1024;
    private byte[]? _buffer;
    private bool _disposed;
    public async ValueTask SendAsync(WebSocket socket, SurfaceUpdate update, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(socket); ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(update.Rgba);
        if (update.X < 0 || update.Y < 0 || update.Width < 1 || update.Height < 1 ||
            (long)update.X + update.Width > 8192 || (long)update.Y + update.Height > 8192 ||
            (long)update.Width * update.Height > 16_777_216 || update.Rgba.LongLength != (long)update.Width * update.Height * 4)
            throw new InvalidDataException("Invalid bridge surface geometry or payload.");
        ct.ThrowIfCancellationRequested();
        int stride = update.Width * 4;
        int rows = Math.Min(64, (MaximumPacketBytes - 24) / stride);
        int capacity = 24 + stride * Math.Min(rows, update.Height);
        if (_buffer is null || _buffer.Length < capacity)
        {
            if (_buffer is not null) ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = ArrayPool<byte>.Shared.Rent(capacity);
        }
        for (int row = 0; row < update.Height; row += rows)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(rows, update.Height - row), length = stride * count;
            Encode(_buffer, update, row, count, length);
            await socket.SendAsync(_buffer.AsMemory(0, length + 24), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
    }
    private static void Encode(byte[] buffer, SurfaceUpdate update, int row, int rows, int length)
    {
        "RSU1"u8.CopyTo(buffer);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), update.X);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), update.Y + row);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), update.Width);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), rows);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(20), length);
        update.Rgba.AsSpan(row * update.Width * 4, length).CopyTo(buffer.AsSpan(24));
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_buffer is not null) ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = null;
    }
}
