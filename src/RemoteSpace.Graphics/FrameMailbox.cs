using RemoteSpace.Core;

namespace RemoteSpace.Graphics;

/// <summary>A presentation snapshot owns its pixels. Size and pixels always refer to the same generation.</summary>
public sealed record FramePresentation(long Generation, DesktopSize Size, SurfaceUpdate? Update);

/// <summary>
/// A bounded latest-frame handoff for a fast decoder and a slower renderer or transport.
/// Rectangles are composited in arrival order, not queued or dropped. Taking a snapshot
/// copies only the union of pending dirty rectangles; later updates never mutate it.
/// Intermediate resize generations may be coalesced, but old pixels cannot enter a new generation.
/// </summary>
public sealed class FrameMailbox : IDisposable
{
    private readonly object _gate = new();
    private readonly FrameBuffer _frame = new();
    private bool _geometryPending, _dirty, _disposed;
    private int _left, _top, _right, _bottom;
    private long _generation;
    public long BufferedPixelBytes { get { lock (_gate) return (long)_frame.Width * _frame.Height * 4; } }
    public void Resize(int width, int height)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _frame.Resize(width, height); _generation = checked(_generation + 1);
            _geometryPending = true; _dirty = false;
        }
    }
    public void Apply(SurfaceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _frame.Apply(update); // Validate before mutating the dirty bounds.
            int right = checked(update.X + update.Width), bottom = checked(update.Y + update.Height);
            if (!_dirty) { _left = update.X; _top = update.Y; _right = right; _bottom = bottom; }
            else { _left = Math.Min(_left, update.X); _top = Math.Min(_top, update.Y); _right = Math.Max(_right, right); _bottom = Math.Max(_bottom, bottom); }
            _dirty = true;
        }
    }
    public bool TryTake(out FramePresentation? presentation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_geometryPending && !_dirty) { presentation = null; return false; }
            var update = _dirty ? _frame.Snapshot(_left, _top, _right - _left, _bottom - _top) : null;
            presentation = new(_generation, new(_frame.Width, _frame.Height), update);
            _geometryPending = false; _dirty = false; return true;
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _frame.Clear(); _dirty = _geometryPending = false;
        }
    }
}
