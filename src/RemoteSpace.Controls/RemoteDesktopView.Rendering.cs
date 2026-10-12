using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RemoteSpace.Core;
using RemoteSpace.Graphics;
using RemoteSpace.Input;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace RemoteSpace.Controls;

public sealed partial class RemoteDesktopView
{
    private sealed class DesktopCanvas : SKCanvasElement, IDisposable
    {
        private readonly object _gate = new();
        private SKBitmap? _bitmap, _cursor;
        private RemoteCursor? _cursorInfo;
        private byte[]? _resolvedCursor;
        private int _cursorX, _cursorY;
        private double _offsetX, _offsetY, _presentedWidth, _presentedHeight;
        public event Action? FrameRequested;
        private bool _pixelsChanged;
        private Viewport _viewport;
        public bool Fit { get; set; } = true;
        public double Zoom { get; set; } = 1;
        public int PixelWidth { get; private set; }
        public int PixelHeight { get; private set; }
        public long AppliedUpdates { get; private set; }
        public long PresentedFrames { get; private set; }
        public long PixelInvalidations { get; private set; }
        public (int Width, int Height) SizeSnapshot() { lock (_gate) return (PixelWidth, PixelHeight); }
        public void MarkDirty() => FrameRequested?.Invoke();
        public void Present() => Invalidate();
        public void ConfigureViewport(bool fit, double zoom, double x, double y)
        {
            lock (_gate)
            {
                Fit = fit; Zoom = zoom; _offsetX = x; _offsetY = y;
                // Do not forward input with a transform which has not been painted.
                _viewport = default;
            }
            MarkDirty();
        }
        public void Clear()
        {
            lock (_gate) { _bitmap?.Dispose(); _bitmap = null; _cursor?.Dispose(); _cursor = null; _cursorInfo = null; _resolvedCursor = null; PixelWidth = PixelHeight = 0; AppliedUpdates = PresentedFrames = PixelInvalidations = 0; _viewport = default; _pixelsChanged = false; }
            MarkDirty();
        }
        public void Resize(int width, int height)
        {
            if (width is < 1 or > 8192 || height is < 1 or > 8192 || (long)width * height > 16_777_216) throw new InvalidDataException("Invalid desktop size.");
            lock (_gate)
            {
                var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                bitmap.Erase(SKColors.Black); _bitmap?.Dispose(); _bitmap = bitmap;
                PixelWidth = width; PixelHeight = height; _viewport = default; _pixelsChanged = false;
            }
            MarkDirty();
        }
        public void Apply(SurfaceUpdate update)
        {
            lock (_gate)
            {
                if (_bitmap is null || update.X < 0 || update.Y < 0 || update.Width < 1 || update.Height < 1 ||
                    (long)update.X + update.Width > PixelWidth || (long)update.Y + update.Height > PixelHeight || update.Rgba.LongLength != (long)update.Width * update.Height * 4)
                    throw new InvalidDataException("Surface update exceeds the negotiated desktop.");
                var address = _bitmap.GetPixels();
                if (update.X == 0 && update.Width * 4 == _bitmap.RowBytes)
                    Marshal.Copy(update.Rgba, 0, address + update.Y * _bitmap.RowBytes, update.Rgba.Length);
                else
                    for (int y = 0; y < update.Height; y++) Marshal.Copy(update.Rgba, y * update.Width * 4,
                        address + (update.Y + y) * _bitmap.RowBytes + update.X * 4, update.Width * 4);
                _pixelsChanged = true; AppliedUpdates++;
            }
            MarkDirty();
        }
        public void SetCursor(RemoteCursor cursor)
        {
            if (!cursor.Hidden && !cursor.SystemDefault) CursorCompositor.Validate(cursor);
            lock (_gate)
            {
                _cursor?.Dispose(); _cursor = null; _cursorInfo = cursor;
                _resolvedCursor = cursor.InvertMask is null ? null : new byte[cursor.Rgba.Length];
                if (!cursor.Hidden && !cursor.SystemDefault)
                {
                    _cursor = new SKBitmap(cursor.Width, cursor.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                    Marshal.Copy(cursor.Rgba, 0, _cursor.GetPixels(), cursor.Rgba.Length); _cursor.NotifyPixelsChanged();
                }
            }
            MarkDirty();
        }
        public void MoveCursor(int x, int y)
        {
            bool repaint;
            lock (_gate)
            {
                repaint = _cursor is not null && (_cursorX != x || _cursorY != y);
                _cursorX = x; _cursorY = y;
            }
            if (repaint) MarkDirty();
        }
        public (ushort X, ushort Y)? Map(Point point, bool clamp)
        {
            lock (_gate)
            {
                return _viewport.MapVisible(point.X, point.Y, PixelWidth, PixelHeight, _presentedWidth, _presentedHeight, clamp);
            }
        }
        private void PublishPixels()
        {
            if (!_pixelsChanged || _bitmap is null) return;
            _bitmap.NotifyPixelsChanged(); _pixelsChanged = false; PixelInvalidations++;
        }
        protected override void RenderOverride(SKCanvas canvas, Size area)
        {
            canvas.Clear(new SKColor(17, 20, 26));
            lock (_gate)
            {
                if (_bitmap is null) return;
                _presentedWidth = area.Width; _presentedHeight = area.Height;
                _viewport = DesktopViewportLayout.Calculate(area.Width, area.Height, PixelWidth, PixelHeight, Fit, Zoom, _offsetX, _offsetY, 0).Image;
                var rect = new SKRect((float)_viewport.X, (float)_viewport.Y, (float)(_viewport.X + _viewport.Width), (float)(_viewport.Y + _viewport.Height));
                PublishPixels(); canvas.DrawBitmap(_bitmap, rect);
                if (_cursor is not null && _cursorInfo is { } cursor)
                {
                    if (_resolvedCursor is not null)
                    {
                        CursorCompositor.Resolve(cursor, _bitmap.GetPixelSpan(), PixelWidth, PixelHeight, _bitmap.RowBytes, _cursorX, _cursorY, _resolvedCursor);
                        Marshal.Copy(_resolvedCursor, 0, _cursor.GetPixels(), _resolvedCursor.Length); _cursor.NotifyPixelsChanged();
                    }
                    float sx = (float)(_viewport.Width / PixelWidth), sy = (float)(_viewport.Height / PixelHeight);
                    float x = rect.Left + (_cursorX - cursor.HotX) * sx, y = rect.Top + (_cursorY - cursor.HotY) * sy;
                    canvas.Save(); canvas.ClipRect(rect);
                    canvas.DrawBitmap(_cursor, new SKRect(x, y, x + cursor.Width * sx, y + cursor.Height * sy));
                    canvas.Restore();
                }
                PresentedFrames++;
            }
        }
        public byte[] CapturePng()
        {
            lock (_gate)
            {
                if (_bitmap is null) throw new InvalidOperationException("No remote frame has been received.");
                PublishPixels();
                using var image = SKImage.FromBitmap(_bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return data.ToArray();
            }
        }
        public new void Dispose() { Clear(); base.Dispose(); }
    }
}
