using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Core;
using RemoteSpace.Profiles;

namespace RemoteSpace.Controls;

/// <summary>Transport-independent profile editor. Passwords are deliberately not part of this control.</summary>
public sealed class ProfileEditor : UserControl
{
    private readonly ConnectionProfile _original;
    private readonly TextBox _name = Field("Friendly name"), _host = Field("PC address"), _group = Field("Group"), _user = Field("User name"), _domain = Field("Domain"), _pin = Field("Certificate SHA-256 pin (optional)");
    private readonly NumberBox _port = new() { Header = "Port", Minimum = 1, Maximum = 65535, Value = 3389, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox _width = new() { Header = "Width", Minimum = 200, Maximum = 8192, Value = 1280 }, _height = new() { Header = "Height", Minimum = 200, Maximum = 8192, Value = 720 };
    private readonly ComboBox _security = new() { Header = "Connection security", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _depth = new() { Header = "Color depth", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _clipboard = new() { Content = "Allow text clipboard redirection (opt-in)" };
    public ProfileEditor(ConnectionProfile? profile = null)
    {
        _original = profile ?? new(); _name.Text = profile?.Name ?? ""; _host.Text = _original.Host; _group.Text = _original.Group;
        _user.Text = _original.UserName; _domain.Text = _original.Domain; _pin.Text = _original.CertificateSha256 ?? "";
        _port.Value = _original.Port; _width.Value = _original.Width; _height.Value = _original.Height;
        _security.Items.Add("Network Level Authentication (recommended)"); _security.Items.Add("TLS (only for hosts explicitly configured without NLA)"); _security.SelectedIndex = (int)_original.Security;
        _depth.Items.Add("24-bit"); _depth.Items.Add("16-bit"); _depth.SelectedIndex = _original.BitsPerPixel == 16 ? 1 : 0;
        _clipboard.IsChecked = _original.RedirectClipboard;
        var body = new StackPanel { Spacing = 14, MinWidth = 250, MaxWidth = 560 };
        _host.PlaceholderText = "Computer name or IP address[:port]";
        AutomationProperties.SetHelpText(_host, "An explicit port in the PC address overrides the Port setting. Use brackets for IPv6 with a port.");
        body.Children.Add(_host);
        var advanced = new StackPanel { Spacing = 12 };
        foreach (var element in new FrameworkElement[] { _name, _port, _group, _user, _domain, _width, _height, _depth, _security, _pin, _clipboard }) advanced.Children.Add(element);
        // Uno Skia can expose zero-sized semantic peers for collapsed Expander
        // content. Detach the content, retaining the controls and their values,
        // so keyboard/screen-reader focus cannot target invisible fields.
        var expansion = new Expander { Header = "Additional settings", IsExpanded = profile is not null, HorizontalAlignment = HorizontalAlignment.Stretch };
        expansion.Expanding += (_, _) => expansion.Content = advanced;
        expansion.Collapsed += (_, _) => expansion.Content = null;
        expansion.Content = expansion.IsExpanded ? advanced : null;
        body.Children.Add(expansion);
        body.Children.Add(new TextBlock { Text = "Passwords are requested only when connecting and are never saved. Certificate trust is checked; unknown certificates require an explicit fingerprint obtained from the administrator.", TextWrapping = TextWrapping.Wrap, Opacity = .7 });
        Content = new ScrollViewer { Content = body, MaxHeight = 530, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    public ConnectionProfile ReadProfile()
    {
        if (_name.Text.Length > 256 || _name.Text.Any(char.IsControl) || _group.Text.Length > 256 || _group.Text.Any(char.IsControl)) throw new ArgumentException("Enter a valid friendly name and group.");
        var endpoint = RdpFile.ParseAddressWithDefaultPort(_host.Text, Integer(_port.Value, "port"));
        var result = _original with
        {
            Name = string.IsNullOrWhiteSpace(_name.Text) ? endpoint.Host : _name.Text.Trim(), Host = endpoint.Host, Port = endpoint.Port, Group = _group.Text.Trim(),
            UserName = _user.Text.Trim(), Domain = _domain.Text.Trim(), Width = Integer(_width.Value, "width"), Height = Integer(_height.Value, "height"),
            BitsPerPixel = _depth.SelectedIndex == 1 ? 16 : 24, Security = (SecurityMode)_security.SelectedIndex,
            CertificateSha256 = string.IsNullOrWhiteSpace(_pin.Text) ? null : _pin.Text.Trim().Replace(":", ""), RedirectClipboard = _clipboard.IsChecked == true
        };
        result.ToOptions().Validate(); return result;
    }
    private static int Integer(double value, string field) => double.IsFinite(value) && value == Math.Truncate(value) && value <= int.MaxValue && value >= int.MinValue ? (int)value : throw new ArgumentException("Enter a whole number for " + field + ".");
    private static TextBox Field(string label) { var box = new TextBox { Header = label }; AutomationProperties.SetName(box, label); return box; }
}
