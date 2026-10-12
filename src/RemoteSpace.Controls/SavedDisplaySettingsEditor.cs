using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Profiles;

namespace RemoteSpace.Controls;

/// <summary>Reusable per-PC display form. Edits are detached from persistence and
/// active sessions; Read updates only display fields in the supplied profile.</summary>
public sealed partial class SavedDisplaySettingsEditor : UserControl
{
    private readonly NumberBox _width = new() { Header = "Width", Minimum = 200, Maximum = 8192 };
    private readonly NumberBox _height = new() { Header = "Height", Minimum = 200, Maximum = 8192 };
    private readonly ComboBox _depth = new() { Header = "Color depth", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _fit = new() { Content = "Fit session to window" };
    private readonly ComboBox _zoom = new() { Header = "Local zoom", DisplayMemberPath = "Label", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _body = new() { Spacing = 12 };
    private sealed record ScaleChoice(double Scale, string Label);
    private readonly ScaleChoice[] _scales;

    public SavedDisplaySettingsEditor(ConnectionProfile? profile = null)
    {
        InitializeComponent();
        profile ??= new();
        _ = profile.GetLocalDisplayScale();
        _scales = new[] { 1d, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, profile.LocalZoom }
            .Distinct().Order().Select(s => new ScaleChoice(s, s.ToString("0.####%", CultureInfo.InvariantCulture))).ToArray();
        _zoom.ItemsSource = _scales;
        AutomationProperties.SetName(_zoom, "Local zoom");
        AutomationProperties.SetHelpText(_zoom, "Local magnification only. This does not change the remote PC's resolution or text size settings.");
        _depth.Items.Add("24-bit"); _depth.Items.Add("16-bit");
        _width.Value = profile.Width; _height.Value = profile.Height;
        _depth.SelectedIndex = profile.BitsPerPixel == 16 ? 1 : 0;
        _fit.IsChecked = profile.FitSessionToWindow;
        _zoom.SelectedItem = _scales.First(s => s.Scale == profile.LocalZoom);
        _zoom.IsEnabled = !_fit.IsChecked.GetValueOrDefault();
        // The pinned browser ComboBox factory omits the initial enabled state.
        // Publish its actual state through the native automation peer after the
        // control is attached, and on changes. No DOM attributes are rewritten.
        _zoom.Loaded += (_, _) => PublishZoomEnabledState();
        _zoom.IsEnabledChanged += (_, _) => { if (_zoom.IsLoaded) PublishZoomEnabledState(); };
        _fit.Checked += (_, _) => _zoom.IsEnabled = false;
        _fit.Unchecked += (_, _) => _zoom.IsEnabled = true;
        _body.Children.Add(new TextBlock { Text = "Display settings", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        _body.Children.Add(new TextBlock { Text = "Single display · Applied the next time you connect.", TextWrapping = TextWrapping.Wrap, Opacity = .7 });
        var size = new Grid { ColumnSpacing = 12 };
        size.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        size.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        size.Children.Add(_width); Grid.SetColumn(_height, 1); size.Children.Add(_height);
        _body.Children.Add(size); _body.Children.Add(_depth); _body.Children.Add(_fit); _body.Children.Add(_zoom);
        _body.Children.Add(new TextBlock
        {
            Text = "Fit adjusts local scaling when the window changes size. The remote resolution stays fixed. Turn Fit off to use a fixed local zoom with scrollbars.",
            TextWrapping = TextWrapping.Wrap, Opacity = .7
        });
        var reset = new Button { Content = "Restore display defaults", HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => RestoreDefaults();
        _body.Children.Add(reset);
        Loaded += (_, _) => SettingsHost.ShowPanel(_body);
        Unloaded += (_, _) => SettingsHost.ShowPanel(null);
    }

    private void PublishZoomEnabledState()
    {
        var peer = FrameworkElementAutomationPeer.FromElement(_zoom) ?? FrameworkElementAutomationPeer.CreatePeerForElement(_zoom);
        peer?.RaisePropertyChangedEvent(AutomationElementIdentifiers.IsEnabledProperty, !_zoom.IsEnabled, _zoom.IsEnabled);
    }

    public void RestoreDefaults()
    {
        _width.Value = 1280; _height.Value = 720; _depth.SelectedIndex = 0;
        _fit.IsChecked = true; _zoom.SelectedItem = _scales.First(s => s.Scale == 1);
    }

    public ConnectionProfile Read(ConnectionProfile original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var result = original with
        {
            Width = Dimension(_width.Value, "Width"), Height = Dimension(_height.Value, "Height"),
            BitsPerPixel = _depth.SelectedIndex == 1 ? 16 : 24,
            FitSessionToWindow = _fit.IsChecked == true,
            LocalZoom = (_zoom.SelectedItem as ScaleChoice)?.Scale ?? throw new ArgumentException("Select a local zoom.")
        };
        _ = result.GetLocalDisplayScale();
        if ((long)result.Width * result.Height > 16_777_216)
            throw new ArgumentException("The requested desktop must not exceed 16 megapixels.");
        return result;
    }

    private static int Dimension(double value, string name) => double.IsFinite(value) && value == Math.Truncate(value) && value is >= 200 and <= 8192
        ? (int)value : throw new ArgumentException(name + " must be a whole number from 200 to 8192 pixels.");
}
