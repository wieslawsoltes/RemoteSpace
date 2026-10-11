using System.Runtime.InteropServices;
using System.Threading.Channels;
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

/// <summary>A reusable Uno/Skia remote surface. Protocol and transport engines are injected,
/// never owned by the control. Pixels are bounded and updated in place; the app compositor presents them.</summary>
public sealed class RemoteDesktopView : ContentControl, IAsyncDisposable
{
    private readonly DesktopCanvas _canvas = new();
    private readonly InputState _input = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private IRemoteSession? _session;
    private Channel<RemoteInput>? _outbox;
    private Task? _writer;
    private readonly Microsoft.UI.Input.InputSystemCursor _arrow = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
    private CancellationTokenSource? _stop;
    private readonly SemaphoreSlim _attachment = new(1);
    public event Action<string>? InputError;
    public int DesktopWidth => _canvas.PixelWidth;
    public int DesktopHeight => _canvas.PixelHeight;
    public long AppliedUpdates => _canvas.AppliedUpdates;
    public long PresentedFrames => _canvas.PresentedFrames;
    public long PixelInvalidations => _canvas.PixelInvalidations;
    public string Renderer => "Uno Skia compositor (hardware acceleration depends on the host)";
    public bool FitToWindow { get => _canvas.Fit; set { _canvas.Fit = value; _canvas.MarkDirty(); } }
    public double Zoom { get => _canvas.Zoom; set { if (!double.IsFinite(value) || value is < .1 or > 4) throw new ArgumentOutOfRangeException(nameof(value)); _canvas.Zoom = value; _canvas.MarkDirty(); } }

    public RemoteDesktopView()
    {
        IsTabStop = true; HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Black); Content = _canvas;
        AutomationProperties.SetName(this, "Remote desktop");
        AutomationProperties.SetHelpText(this, "Remote keyboard and pointer input. Move focus out of the session to release held keys.");
        _refresh.Tick += (_, _) => _canvas.PresentIfDirty();
        Loaded += (_, _) => _refresh.Start();
        Unloaded += (_, _) => { _refresh.Stop(); ReleaseHeldInput(); };
        SizeChanged += (_, _) => _canvas.MarkDirty();
        KeyDown += (_, e) =>
        {
            if (_session?.State != SessionState.Active) return;
#if __WASM__
            // Uno's browser KeyStatus is not implemented; its key mapping still supplies virtual keys.
            var key = _input.KeyDown((uint)e.Key);
#else
            var key = _input.KeyDown((uint)e.Key, e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey);
#endif
            if (key is { } value) { Queue(value); e.Handled = true; }
        };
        KeyUp += (_, e) => { if (_input.KeyUp((uint)e.Key) is { } value) { Queue(value); e.Handled = true; } };
        LostFocus += (_, _) => ReleaseHeldInput();
        PointerCaptureLost += (_, _) => ReleaseHeldInput();
        PointerPressed += (_, e) =>
        {
            if (_session?.State != SessionState.Active) return;
            var p = e.GetCurrentPoint(this); var mapped = _canvas.Map(p.Position, false);
            if (mapped is not { } point) return;
            Focus(FocusState.Pointer); CapturePointer(e.Pointer);
            ushort button = p.Properties.PointerUpdateKind switch
            {
                Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed => 0x1000,
                Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed => 0x2000,
                Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed => 0x4000,
                _ => 0
            };
            if (button != 0 && _input.Button(button, true, point.X, point.Y) is { } value) Queue(value);
            e.Handled = true;
        };
        PointerReleased += (_, e) =>
        {
            var p = e.GetCurrentPoint(this); var mapped = _canvas.Map(p.Position, true);
            if (mapped is { } point)
            {
                ushort button = p.Properties.PointerUpdateKind switch
                {
                    Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased => 0x1000,
                    Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased => 0x2000,
                    Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased => 0x4000,
                    _ => 0
                };
                if (button != 0 && _input.Button(button, false, point.X, point.Y) is { } value) Queue(value);
            }
            if (_input.HeldButtonCount == 0) ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        };
        PointerMoved += (_, e) =>
        {
            if (_session?.State != SessionState.Active) return;
            if (_canvas.Map(e.GetCurrentPoint(this).Position, _input.HeldButtonCount > 0) is { } point)
            { _canvas.MoveCursor(point.X, point.Y); Queue(_input.Move(point.X, point.Y)); e.Handled = true; }
        };
        PointerWheelChanged += (_, e) =>
        {
            if (_session?.State != SessionState.Active) return;
            var p = e.GetCurrentPoint(this);
            if (_canvas.Map(p.Position, false) is { } point)
            { foreach (var input in _input.Wheel(Math.Clamp(p.Properties.MouseWheelDelta, -12000, 12000), point.X, point.Y)) Queue(input); e.Handled = true; }
        };
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new DesktopPeer(this);
    private sealed class DesktopPeer(RemoteDesktopView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(RemoteDesktopView);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override bool IsKeyboardFocusableCore() => true;
    }
    public async Task AttachAsync(IRemoteSession? session)
    {
        await _attachment.WaitAsync();
        try
        {
            if (_session is not null)
            {
                ReleaseHeldInput();
                _session.DesktopResized -= OnSize; _session.SurfaceUpdated -= OnSurface;
                if (_session is IRemoteCursorSource source) { source.CursorChanged -= OnCursor; source.CursorPositionChanged -= OnCursorPosition; }
                _outbox?.Writer.TryComplete();
                try { if (_writer is not null) await _writer.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { _stop?.Cancel(); if (_writer is not null) await _writer; }
            }
            _stop?.Dispose(); _stop = null; _outbox = null; _writer = null; _session = session;
            if (session is null) return;
            _canvas.Clear();
            _stop = new();
            _outbox = Channel.CreateBounded<RemoteInput>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
            _writer = WriteInputAsync(session, _outbox.Reader, _stop.Token);
            session.DesktopResized += OnSize; session.SurfaceUpdated += OnSurface;
            if (session is IRemoteCursorSource cursor) { cursor.CursorChanged += OnCursor; cursor.CursorPositionChanged += OnCursorPosition; }
        }
        finally { _attachment.Release(); }
    }
    public void ReleaseHeldInput() { foreach (var input in _input.ReleaseAll()) Queue(input); }
    public void SendSecureAttention() { ReleaseHeldInput(); foreach (var input in InputState.SecureAttention()) Queue(input); }
    public void SendText(string text) { foreach (var input in InputState.UnicodeText(text)) Queue(input); }
    public byte[] CapturePng() => _canvas.CapturePng();
    private void Queue(RemoteInput input)
    {
        if (_session?.State != SessionState.Active || _outbox is null) return;
        if (!_outbox.Writer.TryWrite(input))
        {
            // Never silently lose key releases on a saturated connection.
            _stop?.Cancel(); _ = _session.DisconnectAsync();
            InputError?.Invoke("Remote input queue exceeded its bound. The session was disconnected to avoid stuck keys.");
        }
    }
    private async Task WriteInputAsync(IRemoteSession session, ChannelReader<RemoteInput> reader, CancellationToken ct)
    {
        try { await foreach (var input in reader.ReadAllAsync(ct)) if (session.State == SessionState.Active) await session.SendInputAsync(input, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            DispatcherQueue.TryEnqueue(() => InputError?.Invoke(error.Message));
            await session.DisconnectAsync();
        }
    }
    private void OnSize(DesktopSize size) => _canvas.Resize(size.Width, size.Height);
    private void OnSurface(SurfaceUpdate update) => _canvas.Apply(update);
    private void OnCursor(RemoteCursor cursor)
    {
        _canvas.SetCursor(cursor);
        DispatcherQueue.TryEnqueue(() => ProtectedCursor = cursor.SystemDefault ? _arrow : null);
    }
    private void OnCursorPosition(ushort x, ushort y) => _canvas.MoveCursor(x, y);
    public async ValueTask DisposeAsync() { _refresh.Stop(); await AttachAsync(null); _canvas.Dispose(); _arrow.Dispose(); }

    private sealed class DesktopCanvas : SKCanvasElement, IDisposable
    {
        private readonly object _gate = new();
        private SKBitmap? _bitmap, _cursor;
        private RemoteCursor? _cursorInfo;
        private byte[]? _resolvedCursor;
        private int _cursorX, _cursorY, _dirty;
        private bool _pixelsChanged;
        private Viewport _viewport;
        public bool Fit { get; set; } = true;
        public double Zoom { get; set; } = 1;
        public int PixelWidth { get; private set; }
        public int PixelHeight { get; private set; }
        public long AppliedUpdates { get; private set; }
        public long PresentedFrames { get; private set; }
        public long PixelInvalidations { get; private set; }
        public void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);
        public void PresentIfDirty() { if (Interlocked.Exchange(ref _dirty, 0) != 0) Invalidate(); }
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
        public void MoveCursor(int x, int y) { lock (_gate) { _cursorX = x; _cursorY = y; } MarkDirty(); }
        public (ushort X, ushort Y)? Map(Point point, bool clamp)
        {
            lock (_gate)
            {
                if (clamp && _viewport.Width > 0 && _viewport.Height > 0) point = new(Math.Clamp(point.X, _viewport.X, _viewport.X + _viewport.Width - .001), Math.Clamp(point.Y, _viewport.Y, _viewport.Y + _viewport.Height - .001));
                return _viewport.Map(point.X, point.Y, PixelWidth, PixelHeight);
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
                _viewport = Fit ? Viewport.Fit(area.Width, area.Height, PixelWidth, PixelHeight) : new Viewport((area.Width - PixelWidth * Zoom) / 2, (area.Height - PixelHeight * Zoom) / 2, PixelWidth * Zoom, PixelHeight * Zoom);
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
