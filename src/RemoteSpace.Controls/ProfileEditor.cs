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
        _original = profile ?? new(); _name.Text = _original.Name; _host.Text = _original.Host; _group.Text = _original.Group;
        _user.Text = _original.UserName; _domain.Text = _original.Domain; _pin.Text = _original.CertificateSha256 ?? "";
        _port.Value = _original.Port; _width.Value = _original.Width; _height.Value = _original.Height;
        _security.Items.Add("Network Level Authentication (recommended)"); _security.Items.Add("TLS (only for hosts explicitly configured without NLA)"); _security.SelectedIndex = (int)_original.Security;
        _depth.Items.Add("24-bit"); _depth.Items.Add("16-bit"); _depth.SelectedIndex = _original.BitsPerPixel == 16 ? 1 : 0;
        _clipboard.IsChecked = _original.RedirectClipboard;
        var body = new StackPanel { Spacing = 12, MinWidth = 350, MaxWidth = 600 };
        foreach (var element in new FrameworkElement[] { _name, _host, _port, _group, _user, _domain, _width, _height, _depth, _security, _pin, _clipboard }) body.Children.Add(element);
        body.Children.Add(new TextBlock { Text = "Passwords are requested only when connecting and are never saved. Certificate trust is checked; unknown certificates require an explicit fingerprint obtained from the administrator.", TextWrapping = TextWrapping.Wrap, Opacity = .7 });
        Content = new ScrollViewer { Content = body, MaxHeight = 530, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    public ConnectionProfile ReadProfile()
    {
        if (string.IsNullOrWhiteSpace(_name.Text) || _name.Text.Length > 256 || _name.Text.Any(char.IsControl) || _group.Text.Length > 256 || _group.Text.Any(char.IsControl)) throw new ArgumentException("Enter a valid friendly name and group.");
        var result = _original with
        {
            Name = _name.Text.Trim(), Host = _host.Text.Trim(), Port = Integer(_port.Value, "port"), Group = _group.Text.Trim(),
            UserName = _user.Text.Trim(), Domain = _domain.Text.Trim(), Width = Integer(_width.Value, "width"), Height = Integer(_height.Value, "height"),
            BitsPerPixel = _depth.SelectedIndex == 1 ? 16 : 24, Security = (SecurityMode)_security.SelectedIndex,
            CertificateSha256 = string.IsNullOrWhiteSpace(_pin.Text) ? null : _pin.Text.Trim().Replace(":", ""), RedirectClipboard = _clipboard.IsChecked == true
        };
        result.ToOptions().Validate(); return result;
    }
    private static int Integer(double value, string field) => double.IsFinite(value) && value == Math.Truncate(value) && value <= int.MaxValue && value >= int.MinValue ? (int)value : throw new ArgumentException("Enter a whole number for " + field + ".");
    private static TextBox Field(string label) { var box = new TextBox { Header = label }; AutomationProperties.SetName(box, label); return box; }
}
