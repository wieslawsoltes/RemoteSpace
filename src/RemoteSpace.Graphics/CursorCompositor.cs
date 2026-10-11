using RemoteSpace.Core;

namespace RemoteSpace.Graphics;

/// <summary>Resolves legacy AND/XOR pointer pixels against an RGBA desktop without modifying it.
/// Only the small pointer rectangle is copied; ordinary alpha pixels remain GPU-compositable.</summary>
public static class CursorCompositor
{
    public static void Validate(RemoteCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (cursor.Width is < 1 or > 384 || cursor.Height is < 1 or > 384 ||
            cursor.HotX < 0 || cursor.HotX >= cursor.Width || cursor.HotY < 0 || cursor.HotY >= cursor.Height ||
            cursor.Rgba is null || cursor.Rgba.LongLength != (long)cursor.Width * cursor.Height * 4 ||
            (cursor.InvertMask is not null && cursor.InvertMask.Length != cursor.Width * cursor.Height))
            throw new ArgumentException("Invalid pointer dimensions, hotspot or pixel buffers.", nameof(cursor));
    }
    public static void Resolve(RemoteCursor cursor, ReadOnlySpan<byte> desktop, int desktopWidth, int desktopHeight,
        int rowStride, int pointerX, int pointerY, Span<byte> destination)
    {
        Validate(cursor);
        if (desktopWidth is < 1 or > 8192 || desktopHeight is < 1 or > 8192 ||
            (long)desktopWidth * desktopHeight > 16_777_216 || rowStride < desktopWidth * 4 ||
            (long)(desktopHeight - 1) * rowStride + desktopWidth * 4 > desktop.Length)
            throw new ArgumentException("Invalid RGBA desktop bounds.", nameof(desktop));
        if (destination.Length != cursor.Rgba.Length || desktop.Overlaps(destination) || cursor.Rgba.AsSpan().Overlaps(destination))
            throw new ArgumentException("Use a separate, exactly-sized pointer output buffer.", nameof(destination));
        cursor.Rgba.CopyTo(destination);
        if (cursor.InvertMask is null) return;
        long left = (long)pointerX - cursor.HotX, top = (long)pointerY - cursor.HotY;
        for (int y = 0; y < cursor.Height; y++) for (int x = 0; x < cursor.Width; x++)
        {
            int pixel = y * cursor.Width + x, offset = pixel * 4;
            if (cursor.InvertMask[pixel] == 0) continue;
            long px = left + x, py = top + y;
            if (px < 0 || py < 0 || px >= desktopWidth || py >= desktopHeight)
            {
                destination[offset + 3] = 0; continue;
            }
            int at = checked((int)py * rowStride + (int)px * 4);
            // A set AND bit preserves the background, then the RGB XOR mask is applied.
            // This is bitwise XOR, NOT Skia's Porter-Duff Xor blend mode.
            destination[offset] = (byte)(desktop[at] ^ cursor.Rgba[offset]);
            destination[offset + 1] = (byte)(desktop[at + 1] ^ cursor.Rgba[offset + 1]);
            destination[offset + 2] = (byte)(desktop[at + 2] ^ cursor.Rgba[offset + 2]);
            destination[offset + 3] = 255;
        }
    }
}
