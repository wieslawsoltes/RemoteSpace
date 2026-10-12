using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using RemoteSpace.Graphics;

namespace RemoteSpace.Controls;

public sealed partial class RemoteDesktopView
{
    private readonly PresentationGate _presentation = new();
    private long _presentationTicket;
    private bool _loaded, _presentationActive = true;
    private bool _fit = true, _updatingViewport;
    private double _zoom = 1, _offsetX, _offsetY;
    private DesktopViewportLayout _viewLayout;
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal, Height = 14, Minimum = 0, SmallChange = 32, Visibility = Visibility.Collapsed };
    private readonly ScrollBar _vertical = new() { Orientation = Orientation.Vertical, Width = 14, Minimum = 0, SmallChange = 32, Visibility = Visibility.Collapsed };
    public event Action? ViewChanged;
    public DesktopViewportLayout ViewLayout => _viewLayout;
    public long ScheduledPresentations { get; private set; }
    /// <summary>Host visibility policy. Decoding still retains the latest pixels;
    /// a hidden view schedules no repaint timer and catches up once reactivated.</summary>
    public bool IsPresentationActive
    {
        get => _presentationActive;
        set { if (_presentationActive == value) return; _presentationActive = value; UpdatePresentationActivity(); }
    }
    private void ConfigureViewport()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        root.Children.Add(_canvas);
        Grid.SetRow(_horizontal, 1); root.Children.Add(_horizontal);
        Grid.SetColumn(_vertical, 1); root.Children.Add(_vertical);
        AutomationProperties.SetName(_horizontal, "Horizontal desktop position");
        AutomationProperties.SetName(_vertical, "Vertical desktop position");
        _horizontal.ValueChanged += (_, _) => { if (!_updatingViewport) PanTo(_horizontal.Value, _offsetY); };
        _vertical.ValueChanged += (_, _) => { if (!_updatingViewport) PanTo(_offsetX, _vertical.Value); };
        Content = root;
    }
    public void PanTo(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        _offsetX = x; _offsetY = y; UpdateViewport();
    }
    /// <summary>Change local zoom while preserving the remote pixel at the viewport
    /// center when possible. Null restores fit. Does not change server resolution.</summary>
    public void SetScale(double? scale)
    {
        if (scale is { } value && (!double.IsFinite(value) || value is < .1 or > 4)) throw new ArgumentOutOfRangeException(nameof(scale));
        double centerX = _viewLayout.Image.Width > 0 ? (_viewLayout.Width / 2 - _viewLayout.Image.X) / _viewLayout.Image.Width * DesktopWidth : 0;
        double centerY = _viewLayout.Image.Height > 0 ? (_viewLayout.Height / 2 - _viewLayout.Image.Y) / _viewLayout.Image.Height * DesktopHeight : 0;
        _fit = scale is null;
        if (scale is { } zoom)
        {
            _zoom = zoom;
            var target = DesktopViewportLayout.Calculate(Math.Max(0, ActualWidth), Math.Max(0, ActualHeight), DesktopWidth, DesktopHeight, false, zoom);
            _offsetX = centerX - target.Width / (2 * zoom); _offsetY = centerY - target.Height / (2 * zoom);
        }
        UpdateViewport();
    }
    private void UpdateViewport()
    {
        if (_disposed || _updatingViewport) return;
        var size = _canvas.SizeSnapshot();
        var layout = DesktopViewportLayout.Calculate(Math.Max(0, ActualWidth), Math.Max(0, ActualHeight), size.Width, size.Height, _fit, _zoom, _offsetX, _offsetY);
        bool changed = layout != _viewLayout;
        _updatingViewport = true;
        try
        {
            _offsetX = layout.HorizontalOffset; _offsetY = layout.VerticalOffset; _viewLayout = layout;
            _horizontal.Visibility = layout.HorizontalScroll ? Visibility.Visible : Visibility.Collapsed;
            _vertical.Visibility = layout.VerticalScroll ? Visibility.Visible : Visibility.Collapsed;
            _horizontal.Maximum = layout.MaximumHorizontalOffset; _vertical.Maximum = layout.MaximumVerticalOffset;
            _horizontal.ViewportSize = _horizontal.LargeChange = layout.Width / _zoom;
            _vertical.ViewportSize = _vertical.LargeChange = layout.Height / _zoom;
            _horizontal.Value = _offsetX; _vertical.Value = _offsetY;
            _canvas.ConfigureViewport(_fit, _zoom, _offsetX, _offsetY);
        }
        finally { _updatingViewport = false; }
        if (changed) ViewChanged?.Invoke();
    }
    private bool IsScrollBarInput(object source)
    {
        for (DependencyObject? current = source as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, _horizontal) || ReferenceEquals(current, _vertical)) return true;
            if (ReferenceEquals(current, this)) break;
        }
        return false;
    }
    private void UpdatePresentationActivity()
    {
        if (_disposed) return;
        bool active = _loaded && _presentationActive;
        if (!active) _refresh.Stop();
        SchedulePresentation(_presentation.SetActive(active));
    }
    private void RequestPresentation() => SchedulePresentation(_presentation.Request());
    private void SchedulePresentation(long ticket)
    {
        if (ticket == 0) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed && _presentation.IsScheduled(ticket)) { _presentationTicket = ticket; _refresh.Start(); }
        });
    }
}
