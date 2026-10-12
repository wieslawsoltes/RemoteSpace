using System.Globalization;

namespace RemoteSpace.Core;

/// <summary>Shared strict custom-resolution validation. No rounding, truncation,
/// automatic downgrade or framebuffer allocation occurs while parsing user input.</summary>
public static class DisplayResolution
{
    public static DisplayMonitor Parse(string? width, string? height, DisplayCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        int w = Dimension(width, "Width"), h = Dimension(height, "Height");
        if ((w & 1) != 0) throw new FormatException("Width must be an even number of pixels.");
        var monitor = new DisplayMonitor(0, 0, w, h);
        DisplayLayout.Validate([monitor], capabilities);
        return monitor;
    }

    private static int Dimension(string? text, string name)
    {
        if (text is null || text.Length > 16 || text.Contains('\0') ||
            !int.TryParse(text.AsSpan().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ||
            value is < 200 or > 8192)
            throw new FormatException(name + " must be a whole number from 200 to 8192 pixels.");
        return value;
    }
}
