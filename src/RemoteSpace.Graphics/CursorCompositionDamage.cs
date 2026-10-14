using RemoteSpace.Core;

namespace RemoteSpace.Graphics;

/// <summary>Bounded damage state for a retained AND/XOR cursor texture. Only
/// changes beneath background-dependent cursor pixels require recomposition.
/// The rendering owner must serialize mutations and acknowledge only after a
/// successful resolve/upload. This class retains no pixel buffers or history.</summary>
public sealed class CursorCompositionDamage
{
    private int _x, _y, _left, _top, _right, _bottom;
    private bool _dependent, _dirty;
    public bool NeedsResolve => _dependent && _dirty;

    /// <summary>Replaces the shape and invalidates even a repeated instance:
    /// callers may explicitly reoffer changed source pixels. Hidden/system
    /// cursors have no texture. Alpha-only and all-zero masks need no CPU pass.</summary>
    public void SetCursor(RemoteCursor cursor, int pointerX, int pointerY)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        bool visible = !cursor.Hidden && !cursor.SystemDefault;
        if (visible) CursorCompositor.Validate(cursor);
        int left = cursor.Width, top = cursor.Height, right = 0, bottom = 0;
        if (visible && cursor.InvertMask is { } mask)
        {
            for (int y = 0; y < cursor.Height; y++)
                for (int x = 0; x < cursor.Width; x++)
                    if (mask[y * cursor.Width + x] != 0)
                    {
                        left = Math.Min(left, x); top = Math.Min(top, y);
                        right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
                    }
        }
        // Commit only after validation/scanning. Offsets are small, while the
        // pointer itself may be any int; intersection arithmetic below is long.
        _dependent = visible && right > left && bottom > top;
        _left = _dependent ? left - cursor.HotX : 0;
        _top = _dependent ? top - cursor.HotY : 0;
        _right = _dependent ? right - cursor.HotX : 0;
        _bottom = _dependent ? bottom - cursor.HotY : 0;
        _x = pointerX; _y = pointerY; _dirty = _dependent;
    }

    public void MoveCursor(int x, int y)
    {
        if (_x == x && _y == y) return;
        _x = x; _y = y;
        if (_dependent) _dirty = true;
    }

    /// <summary>Call after a successful desktop write. Bounds are half-open;
    /// merely touching the dependent rectangle does not invalidate it.</summary>
    public void Invalidate(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (!_dependent || _dirty) return;
        if ((long)x < (long)_x + _right && (long)x + width > (long)_x + _left &&
            (long)y < (long)_y + _bottom && (long)y + height > (long)_y + _top)
            _dirty = true;
    }

    /// <summary>Backing desktop replacement invalidates even unchanged dimensions.</summary>
    public void InvalidateAll() { if (_dependent) _dirty = true; }
    public void MarkResolved() => _dirty = false;
    public void Clear() { _dependent = _dirty = false; _x = _y = _left = _top = _right = _bottom = 0; }
}
