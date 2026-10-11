using System.Buffers;

namespace RemoteSpace.Core;

/// <summary>Reads a bounded payload, including from non-seekable or growing streams.
/// Size metadata is not a substitute for enforcing the limit during the read.</summary>
public static class BoundedInput
{
    public static async Task<byte[]> ReadAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var output = new MemoryStream();
        try
        {
            while (true)
            {
                // Read at most one byte past the limit, without overflowing int.
                int wanted = (int)Math.Min(buffer.Length, (long)maximumBytes - output.Length + 1);
                int read = await stream.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                if (read == 0) return output.ToArray();
                if (output.Length + read > maximumBytes) throw new InvalidDataException("Input exceeds the permitted byte limit.");
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            if (output.TryGetBuffer(out var segment) && segment.Array is not null) Array.Clear(segment.Array);
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
