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

/// <summary>Reusable Uno/Skia surface. The host owns the injected session;
/// the control owns presentation and an ordered bounded input queue.</summary>
public sealed partial class RemoteDesktopView : ContentControl, IAsyncDisposable
{
    private readonly DesktopCanvas _canvas = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Microsoft.UI.Input.InputSystemCursor _arrow = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
    private readonly SemaphoreSlim _attachment = new(1);
    private readonly object _events = new();
    private IRemoteSession? _session;
    private RemoteInputQueue? _outbox;
    private Task? _writer;
    private CancellationTokenSource? _stop;
    private Action<DesktopSize>? _onSize;
    private Action<SurfaceUpdate>? _onSurface;
    private Action<RemoteCursor>? _onCursor;
    private Action<ushort,ushort>? _onPosition;
    private Action<SessionState>? _onState;
    private bool _disposed;
    public event Action<string>? InputError;
    public int DesktopWidth => _canvas.PixelWidth;
    public int DesktopHeight => _canvas.PixelHeight;
    public long AppliedUpdates => _canvas.AppliedUpdates;
    public long PresentedFrames => _canvas.PresentedFrames;
    public long PixelInvalidations => _canvas.PixelInvalidations;
    public string Renderer => "Uno Skia compositor (hardware acceleration depends on the host)";
    public bool FitToWindow { get => _fit; set { _fit = value; UpdateViewport(); } }
    public double Zoom { get => _zoom; set { if (!double.IsFinite(value) || value is < .1 or > 4) throw new ArgumentOutOfRangeException(nameof(value)); _zoom = value; UpdateViewport(); } }
    public byte[] CapturePng() => _canvas.CapturePng();
    public RemoteDesktopView()
    {
        IsTabStop = true; HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
        ConfigureViewport();
        _canvas.FrameRequested += RequestPresentation;
        AutomationProperties.SetName(this, "Remote desktop");
        AutomationProperties.SetHelpText(this, "Remote keyboard and pointer input. Move focus out of the session to release held keys.");
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            if (_presentation.Take(_presentationTicket)) { ScheduledPresentations++; _canvas.Present(); }
        };
        Loaded += (_, _) => { _loaded = true; UpdateViewport(); UpdatePresentationActivity(); };
        Unloaded += (_, _) => { _loaded = false; UpdatePresentationActivity(); ReleaseHeldInput(); };
        SizeChanged += (_, _) => UpdateViewport();
        ConfigureInput();
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
            if (_disposed && session is not null) throw new ObjectDisposedException(nameof(RemoteDesktopView));
            if (_session is { } old)
            {
                ReleaseHeldInput(); _outbox?.Complete();
                try { if (_writer is not null) await _writer.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException)
                {
                    _stop?.Cancel(); await old.DisconnectAsync();
                    if (_writer is not null) await _writer.WaitAsync(TimeSpan.FromSeconds(2));
                }
                old.DesktopResized -= _onSize; old.SurfaceUpdated -= _onSurface; old.StateChanged -= _onState;
                if (old is IRemoteCursorSource cursor) { cursor.CursorChanged -= _onCursor; cursor.CursorPositionChanged -= _onPosition; }
            }
            _stop?.Dispose(); _stop = null; _outbox = null; _writer = null;
            lock (_events) { _session = session; _canvas.Clear(); }
            _input.ReleaseAll(); ProtectedCursor = _arrow;
            UpdateViewport();
            if (session is null) return;
            _stop = new(); _outbox = new();
            _onSize = size =>
            {
                lock (_events) if (ReferenceEquals(_session, session)) _canvas.Resize(size.Width, size.Height);
                DispatcherQueue.TryEnqueue(() => { if (ReferenceEquals(_session, session)) UpdateViewport(); });
            };
            _onSurface = update => { lock (_events) if (ReferenceEquals(_session, session)) _canvas.Apply(update); };
            _onCursor = cursor =>
            {
                lock (_events) if (ReferenceEquals(_session, session)) _canvas.SetCursor(cursor);
                DispatcherQueue.TryEnqueue(() => { if (ReferenceEquals(_session, session)) ProtectedCursor = cursor.SystemDefault ? _arrow : null; });
            };
            _onPosition = (x,y) => { lock (_events) if (ReferenceEquals(_session, session)) _canvas.MoveCursor(x,y); };
            _onState = state => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_session, session)) return;
                if (state != SessionState.Active) ReleaseHeldInput();
                if (state is SessionState.Disconnected or SessionState.Faulted) { _outbox?.Complete(); _stop?.Cancel(); }
            });
            session.DesktopResized += _onSize; session.SurfaceUpdated += _onSurface; session.StateChanged += _onState;
            if (session is IRemoteCursorSource source) { source.CursorChanged += _onCursor; source.CursorPositionChanged += _onPosition; }
            _writer = WriteInputAsync(session, _outbox, _stop.Token);
        }
        finally { _attachment.Release(); }
    }
    private Task? _disposal;
    public ValueTask DisposeAsync() => new(_disposal ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        if (_disposed) return; _disposed = true; _refresh.Stop(); _presentation.Dispose();
        await AttachAsync(null); _canvas.Dispose(); _arrow.Dispose();
    }
}
