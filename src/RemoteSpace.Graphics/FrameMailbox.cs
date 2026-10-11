using RemoteSpace.Core;

namespace RemoteSpace.Graphics;

/// <summary>A presentation snapshot owns its pixels. Size and pixels belong to the same generation.</summary>
public sealed record FramePresentation(long Generation, DesktopSize Size, SurfaceUpdate? Update);
public sealed record FrameBatch(long Generation, DesktopSize Size, SurfaceUpdate[] Updates);

/// <summary>Bounded latest-frame handoff. Decoder writes are composited, never queued.
/// The sparse API tracks exact dirty bounds in fixed-height bands and merges matching
/// adjacent bands. Storage and snapshot count do not grow with the update count.</summary>
public sealed class FrameMailbox : IDisposable
{
    public const int BandHeight = 32;
    public const int MaximumRegions = 8192 / BandHeight;
    private readonly object _gate = new();
    private readonly FrameBuffer _frame = new();
    private readonly Band[] _bands = new Band[MaximumRegions];
    private bool _geometryPending, _dirty, _disposed;
    private int _left, _top, _right, _bottom;
    private long _generation;
    public long BufferedPixelBytes { get { lock (_gate) return (long)_frame.Width * _frame.Height * 4; } }
    public long SnapshotPixelBytes { get; private set; }
    public void Resize(int width, int height)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _frame.Resize(width, height); _generation = checked(_generation + 1);
            _geometryPending = true; _dirty = false; Array.Clear(_bands);
        }
    }
    public void Apply(SurfaceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _frame.Apply(update); // Validate before changing any dirty-region state.
            int right = update.X + update.Width, bottom = update.Y + update.Height;
            if (!_dirty) { _left = update.X; _top = update.Y; _right = right; _bottom = bottom; }
            else { _left = Math.Min(_left, update.X); _top = Math.Min(_top, update.Y); _right = Math.Max(_right, right); _bottom = Math.Max(_bottom, bottom); }
            for (int i = update.Y / BandHeight; i <= (bottom - 1) / BandHeight; i++)
            {
                ref Band b = ref _bands[i];
                int top = Math.Max(update.Y, i * BandHeight), end = Math.Min(bottom, (i + 1) * BandHeight);
                if (b.Right == 0) b = new(update.X, top, right, end);
                else b = new(Math.Min(b.Left, update.X), Math.Min(b.Top, top), Math.Max(b.Right, right), Math.Max(b.Bottom, end));
            }
            _dirty = true;
        }
    }
    /// <summary>Compatibility API: takes a single dirty-union snapshot.</summary>
    public bool TryTake(out FramePresentation? presentation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_geometryPending && !_dirty) { presentation = null; return false; }
            var update = _dirty ? Snapshot(new(_left, _top, _right, _bottom)) : null;
            presentation = new(_generation, new(_frame.Width, _frame.Height), update);
            Drain(); return true;
        }
    }
    /// <summary>Atomically takes at most 256 disjoint sparse rectangles. Full repaints
    /// merge to one rectangle. An in-flight batch cannot be mutated by later writes.</summary>
    public bool TryTakeBatch(out FrameBatch? presentation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_geometryPending && !_dirty) { presentation = null; return false; }
            var regions = new List<SurfaceUpdate>(); Band pending = default;
            foreach (var b in _bands)
            {
                if (b.Right == 0) continue;
                if (pending.Right != 0 && pending.Left == b.Left && pending.Right == b.Right && pending.Bottom == b.Top)
                    pending = pending with { Bottom = b.Bottom };
                else
                {
                    if (pending.Right != 0) regions.Add(Snapshot(pending));
                    pending = b;
                }
            }
            if (pending.Right != 0) regions.Add(Snapshot(pending));
            presentation = new(_generation, new(_frame.Width, _frame.Height), regions.ToArray());
            Drain(); return true;
        }
    }
    private SurfaceUpdate Snapshot(Band b)
    {
        var update = _frame.Snapshot(b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top);
        SnapshotPixelBytes += update.Rgba.Length; return update;
    }
    private void Drain() { _geometryPending = _dirty = false; Array.Clear(_bands); }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _frame.Clear(); Drain();
        }
    }
    private readonly record struct Band(int Left, int Top, int Right, int Bottom);
}
