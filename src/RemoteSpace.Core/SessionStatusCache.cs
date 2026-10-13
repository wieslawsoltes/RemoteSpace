using System.Globalization;

namespace RemoteSpace.Core;

/// <summary>One-entry UI-thread cache for session status text. Local pan and fit
/// geometry are intentionally absent: neither changes the displayed status.
/// Immutable numeric formats allow unchanged updates to avoid formatting and
/// allocation; mutable formats are re-evaluated so culture edits cannot go stale.</summary>
public sealed class SessionStatusCache
{
    private (SessionState State, int Width, int Height, double Scale) _previous;
    private NumberFormatInfo? _format;
    private bool _initialized;
    public string Text { get; private set; } = string.Empty;

    /// <summary>Returns true only when the visible text changes. Zero dimensions
    /// describe a session which has not received a desktop yet. Fit ignores the
    /// zoom argument, whereas an explicit zoom must be finite and positive.</summary>
    public bool Update(SessionState state, int width, int height, bool fit, double zoom = 1, CultureInfo? culture = null)
    {
        if ((uint)state > (uint)SessionState.Faulted) throw new ArgumentOutOfRangeException(nameof(state));
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if (!fit && (!double.IsFinite(zoom) || zoom <= 0 || zoom > double.MaxValue / 100))
            throw new ArgumentOutOfRangeException(nameof(zoom));
        var value = (state, width, height, fit ? 0 : zoom);
        var format = (culture ?? CultureInfo.CurrentCulture).NumberFormat;
        if (_initialized && value == _previous && ReferenceEquals(format, _format) && format.IsReadOnly) return false;
        string scale = fit ? "Fit" : zoom.ToString("0%", format);
        string text = string.Create(format, $"{state} · {width} × {height} · {scale}");
        _previous = value; _format = format; _initialized = true;
        if (Text == text) return false;
        Text = text; return true;
    }
}
