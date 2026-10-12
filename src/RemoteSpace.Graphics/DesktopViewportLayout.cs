namespace RemoteSpace.Graphics;

/// <summary>Allocation-free local display layout. Offsets are remote pixels; sizes
/// are host DIPs. Scrollbar interdependence is solved before clamping. This never
/// changes the negotiated server resolution or allocates a zoom-sized framebuffer.</summary>
public readonly record struct DesktopViewportLayout(Viewport Image, double Width, double Height,
    double HorizontalOffset, double VerticalOffset, double MaximumHorizontalOffset,
    double MaximumVerticalOffset, bool HorizontalScroll, bool VerticalScroll)
{
    public static DesktopViewportLayout Calculate(double width, double height, int desktopWidth, int desktopHeight,
        bool fit, double zoom = 1, double horizontalOffset = 0, double verticalOffset = 0, double scrollBarSize = 14)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 0 || height < 0 ||
            !double.IsFinite(zoom) || zoom is < .1 or > 4 || !double.IsFinite(horizontalOffset) ||
            !double.IsFinite(verticalOffset) || !double.IsFinite(scrollBarSize) || scrollBarSize is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(width), "Viewport sizes, offsets and zoom must be finite and in range.");
        if (desktopWidth < 0 || desktopHeight < 0 || desktopWidth > 8192 || desktopHeight > 8192 ||
            (long)desktopWidth * desktopHeight > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(desktopWidth));
        if (width == 0 || height == 0 || desktopWidth == 0 || desktopHeight == 0) return default;
        if (fit) return new(Viewport.Fit(width, height, desktopWidth, desktopHeight), width, height, 0, 0, 0, 0, false, false);
        bool horizontal = false, vertical = false;
        double areaWidth = width, areaHeight = height;
        // A bar can make the other axis overflow. Presence is monotonic within
        // this calculation; three passes reach the two-axis fixed point.
        for (int i = 0; i < 3; i++)
        {
            horizontal = desktopWidth * zoom > areaWidth;
            vertical = desktopHeight * zoom > areaHeight;
            areaWidth = Math.Max(0, width - (vertical ? scrollBarSize : 0));
            areaHeight = Math.Max(0, height - (horizontal ? scrollBarSize : 0));
        }
        double maximumX = Math.Max(0, desktopWidth - areaWidth / zoom);
        double maximumY = Math.Max(0, desktopHeight - areaHeight / zoom);
        double x = Math.Clamp(horizontalOffset, 0, maximumX), y = Math.Clamp(verticalOffset, 0, maximumY);
        var image = new Viewport(horizontal ? -x * zoom : (areaWidth - desktopWidth * zoom) / 2,
            vertical ? -y * zoom : (areaHeight - desktopHeight * zoom) / 2,
            desktopWidth * zoom, desktopHeight * zoom);
        return new(image, areaWidth, areaHeight, x, y, maximumX, maximumY, horizontal, vertical);
    }
}
