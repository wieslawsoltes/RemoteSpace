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
            byte[] pixels = new byte[checked(width * height * 4)];
            Array.Clear(_pixels); Width = width; Height = height; _pixels = pixels;
            for (int i = 3; i < _pixels.Length; i += 4) _pixels[i] = 255;
            Revision++;
        }
    }
    public void Apply(SurfaceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(update.Rgba);
        lock (_gate)
        {
            if (update.Width < 1 || update.Height < 1 || update.X < 0 || update.Y < 0 ||
                (long)update.X + update.Width > Width || (long)update.Y + update.Height > Height ||
                update.Rgba.LongLength != (long)update.Width * update.Height * 4)
                throw new ArgumentException("Bitmap update is outside the desktop or has an invalid payload.", nameof(update));
            if (update.X == 0 && update.Width == Width)
                update.Rgba.AsSpan().CopyTo(_pixels.AsSpan(update.Y * Width * 4, update.Rgba.Length));
            else
                for (int row = 0; row < update.Height; row++)
                    update.Rgba.AsSpan(row * update.Width * 4, update.Width * 4)
                        .CopyTo(_pixels.AsSpan(((update.Y + row) * Width + update.X) * 4));
            Revision++;
        }
    }
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_pixels); _pixels = []; Width = Height = 0; Revision++;
        }
    }
    public SurfaceUpdate Snapshot(int x, int y, int width, int height)
    {
        lock (_gate)
        {
            if (x < 0 || y < 0 || width < 1 || height < 1 || (long)x + width > Width || (long)y + height > Height)
                throw new ArgumentOutOfRangeException(nameof(width), "Snapshot rectangle is outside the desktop.");
            byte[] copy = new byte[checked(width * height * 4)];
            if (x == 0 && width == Width)
                _pixels.AsSpan(y * Width * 4, copy.Length).CopyTo(copy);
            else
                for (int row = 0; row < height; row++)
                    _pixels.AsSpan(((y + row) * Width + x) * 4, width * 4).CopyTo(copy.AsSpan(row * width * 4));
            return new(x, y, width, height, copy);
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
        if (!double.IsFinite(areaWidth) || !double.IsFinite(areaHeight) || areaWidth <= 0 || areaHeight <= 0 || pixelWidth <= 0 || pixelHeight <= 0) return default;
        double scale = Math.Min(areaWidth / pixelWidth, areaHeight / pixelHeight);
        return new((areaWidth - pixelWidth * scale) / 2, (areaHeight - pixelHeight * scale) / 2, pixelWidth * scale, pixelHeight * scale);
    }
    /// <summary>Maps only the visible host area. Captured drags clamp to its
    /// intersection with the image, never to off-screen remote pixels.</summary>
    public (ushort X, ushort Y)? MapVisible(double x, double y, int desktopWidth, int desktopHeight,
        double areaWidth, double areaHeight, bool clamp = false)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(areaWidth) || !double.IsFinite(areaHeight) ||
            !double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Width) || !double.IsFinite(Height) ||
            areaWidth <= 0 || areaHeight <= 0 || Width <= 0 || Height <= 0) return null;
        double left = Math.Max(0, X), top = Math.Max(0, Y);
        double right = Math.Min(areaWidth, X + Width), bottom = Math.Min(areaHeight, Y + Height);
        if (right <= left || bottom <= top) return null;
        if (clamp)
        {
            x = Math.Clamp(x, left, right - Math.Min(1e-6, (right - left) / 2));
            y = Math.Clamp(y, top, bottom - Math.Min(1e-6, (bottom - top) / 2));
        }
        else if (x < left || y < top || x >= right || y >= bottom) return null;
        return Map(x, y, desktopWidth, desktopHeight);
    }
    public (ushort X, ushort Y)? Map(double x, double y, int desktopWidth, int desktopHeight)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(X) || !double.IsFinite(Y) ||
            !double.IsFinite(Width) || !double.IsFinite(Height) || Width <= 0 || Height <= 0 ||
            desktopWidth is < 1 or > 65535 || desktopHeight is < 1 or > 65535 ||
            x < X || y < Y || x >= X + Width || y >= Y + Height) return null;
        return ((ushort)Math.Clamp((int)((x - X) * desktopWidth / Width), 0, desktopWidth - 1),
                (ushort)Math.Clamp((int)((y - Y) * desktopHeight / Height), 0, desktopHeight - 1));
    }
}
