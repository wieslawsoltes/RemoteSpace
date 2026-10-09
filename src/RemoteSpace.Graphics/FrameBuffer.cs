using RemoteSpace.Core;

namespace RemoteSpace.Graphics;

/// <summary>Reusable RGBA surface. Writes and snapshots are synchronized; callers never receive its mutable storage.</summary>
public sealed class FrameBuffer
{
    private readonly object _gate = new();
    private byte[] _pixels = [];
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long Revision { get; private set; }
    public void Resize(int width, int height)
    {
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || (long)width * height > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(width));
        lock (_gate)
        {
            Width = width; Height = height; _pixels = new byte[checked(width * height * 4)];
            for (int i = 3; i < _pixels.Length; i += 4) _pixels[i] = 255;
            Revision++;
        }
    }
    public void Apply(SurfaceUpdate update)
    {
        lock (_gate)
        {
            if (update.Width < 1 || update.Height < 1 || update.X < 0 || update.Y < 0 ||
                (long)update.X + update.Width > Width || (long)update.Y + update.Height > Height ||
                update.Rgba.LongLength != (long)update.Width * update.Height * 4)
                throw new ArgumentException("Bitmap update is outside the desktop or has an invalid payload.", nameof(update));
            for (int row = 0; row < update.Height; row++)
                update.Rgba.AsSpan(row * update.Width * 4, update.Width * 4)
                    .CopyTo(_pixels.AsSpan(((update.Y + row) * Width + update.X) * 4));
            Revision++;
        }
    }
    public SurfaceUpdate Snapshot()
    {
        lock (_gate) return new(0, 0, Width, Height, (byte[])_pixels.Clone());
    }
}

public readonly record struct Viewport(double X, double Y, double Width, double Height)
{
    public static Viewport Fit(double areaWidth, double areaHeight, int pixelWidth, int pixelHeight)
    {
        if (areaWidth <= 0 || areaHeight <= 0 || pixelWidth <= 0 || pixelHeight <= 0) return default;
        double scale = Math.Min(areaWidth / pixelWidth, areaHeight / pixelHeight);
        return new((areaWidth - pixelWidth * scale) / 2, (areaHeight - pixelHeight * scale) / 2, pixelWidth * scale, pixelHeight * scale);
    }
    public (ushort X, ushort Y)? Map(double x, double y, int desktopWidth, int desktopHeight)
    {
        if (Width <= 0 || Height <= 0 || x < X || y < Y || x >= X + Width || y >= Y + Height) return null;
        return ((ushort)Math.Clamp((int)((x - X) * desktopWidth / Width), 0, desktopWidth - 1),
                (ushort)Math.Clamp((int)((y - Y) * desktopHeight / Height), 0, desktopHeight - 1));
    }
}
