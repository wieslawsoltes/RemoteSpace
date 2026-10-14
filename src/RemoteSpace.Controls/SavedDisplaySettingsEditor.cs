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
    private readonly CheckBox _automatic = new() { Content = "Update the resolution on resize" };
    private readonly Grid _size = new() { ColumnSpacing = 12 };
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
        _automatic.IsChecked = profile.UpdateResolutionOnResize;
        _fit.IsChecked = profile.FitSessionToWindow;
        _zoom.SelectedItem = _scales.First(s => s.Scale == profile.LocalZoom);
        _automatic.Checked += (_, _) => UpdateAvailability();
        _automatic.Unchecked += (_, _) => UpdateAvailability();
        _fit.Checked += (_, _) => UpdateAvailability();
        _fit.Unchecked += (_, _) => UpdateAvailability();
        foreach (var control in new Control[] { _fit, _zoom })
        {
            control.Loaded += (_, _) => PublishEnabledState(control);
            control.IsEnabledChanged += (_, _) => { if (control.IsLoaded) PublishEnabledState(control); };
        }
        _body.Children.Add(new TextBlock { Text = "Display settings", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        _body.Children.Add(new TextBlock { Text = "Single display · Applied the next time you connect.", TextWrapping = TextWrapping.Wrap, Opacity = .7 });
        _body.Children.Add(_automatic);
        _size.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _size.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _size.Children.Add(_width); Grid.SetColumn(_height, 1); _size.Children.Add(_height);
        _body.Children.Add(_size); _body.Children.Add(_depth); _body.Children.Add(_fit); _body.Children.Add(_zoom);
        _body.Children.Add(new TextBlock
        {
            Text = "Automatic resolution changes remote pixels to match the window. With a fixed resolution, Fit scales the image locally; turn Fit off to choose a local zoom.",
            TextWrapping = TextWrapping.Wrap, Opacity = .7
        });
        var reset = new Button { Content = "Restore display defaults", HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => RestoreDefaults();
        _body.Children.Add(reset);
        UpdateAvailability();
        Loaded += (_, _) => SettingsHost.ShowPanel(_body);
        Unloaded += (_, _) => SettingsHost.ShowPanel(null);
    }

    private void UpdateAvailability()
    {
        bool automatic = _automatic.IsChecked == true;
        _size.Visibility = automatic ? Visibility.Collapsed : Visibility.Visible;
        _fit.IsEnabled = !automatic;
        _zoom.IsEnabled = !automatic && _fit.IsChecked != true;
    }

    private static void PublishEnabledState(Control control)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(control) ?? FrameworkElementAutomationPeer.CreatePeerForElement(control);
        peer?.RaisePropertyChangedEvent(AutomationElementIdentifiers.IsEnabledProperty, !control.IsEnabled, control.IsEnabled);
    }

    public void RestoreDefaults()
    {
        _width.Value = 1280; _height.Value = 720; _depth.SelectedIndex = 0;
        _automatic.IsChecked = false;
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
            UpdateResolutionOnResize = _automatic.IsChecked == true,
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
