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
    private readonly TextBox _host = Field("PC address");
    private readonly Expander _expansion = new() { Header = "Additional settings", HorizontalAlignment = HorizontalAlignment.Stretch };
    private AdditionalFields? _fields;
    private StackPanel? _panel;

    public ProfileEditor(ConnectionProfile? profile = null)
    {
        _original = profile ?? new ConnectionProfile { Name = "" };
        _host.Text = _original.Host;
        _host.PlaceholderText = "Computer name or IP address[:port]";
        AutomationProperties.SetHelpText(_host, "An explicit port in the PC address overrides the Port setting. Use brackets for IPv6 with a port.");
        var body = new StackPanel { Spacing = 14, MinWidth = 250, MaxWidth = 560 };
        body.Children.Add(_host);
        body.Children.Add(_expansion);
        body.Children.Add(new TextBlock
        {
            Text = "Passwords are requested only when connecting and are never saved. Certificate trust is checked; unknown certificates require an explicit fingerprint obtained from the administrator.",
            TextWrapping = TextWrapping.Wrap, Opacity = .7
        });
        Content = new ScrollViewer { Content = body, MaxHeight = 530, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _expansion.Expanding += (_, _) => ShowAdditionalSettings();
        _expansion.Collapsed += (_, _) => HideAdditionalSettings();
        // Do not construct detached input controls eagerly. The pinned Skia host
        // registers their semantic peers before they ever enter the visual tree.
        // Editing expands after the actual parent is loaded; a minimal new-PC
        // dialog allocates no additional inputs and creates no hidden peers.
        Loaded += (_, _) => { if (profile is not null) _expansion.IsExpanded = true; };
    }

    private void ShowAdditionalSettings()
    {
        if (_panel is not null) return;
        _panel = new StackPanel { Spacing = 12 };
        // A named container supplies a stable semantic parent instead of allowing
        // detached fields to fall back to the application's accessibility root.
        AutomationProperties.SetName(_panel, "Additional PC settings");
        _expansion.Content = _panel;
        _fields ??= new AdditionalFields(_original);
        foreach (var field in _fields.Controls) _panel.Children.Add(field);
    }

    private void HideAdditionalSettings()
    {
        if (_panel is null) return;
        // Remove children while their owning parent is still attached. Retain the
        // control values for a later expansion, without mutating detached peers.
        _panel.Children.Clear();
        _expansion.Content = null;
        _panel = null;
    }

    public ConnectionProfile ReadProfile()
    {
        var result = _fields?.Read(_original) ?? _original;
        if (result.Name.Length > 256 || result.Name.Any(char.IsControl) || result.Group.Length > 256 || result.Group.Any(char.IsControl))
            throw new ArgumentException("Enter a valid friendly name and group.");
        var endpoint = RdpFile.ParseAddressWithDefaultPort(_host.Text, result.Port);
        result = result with { Name = string.IsNullOrWhiteSpace(result.Name) ? endpoint.Host : result.Name.Trim(), Host = endpoint.Host, Port = endpoint.Port };
        result.ToOptions().Validate();
        return result;
    }

    private sealed class AdditionalFields
    {
        private readonly TextBox _name = Field("Friendly name"), _group = Field("Group"), _user = Field("User name"), _domain = Field("Domain"), _pin = Field("Certificate SHA-256 pin (optional)");
        private readonly NumberBox _port = new() { Header = "Port", Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        private SavedDisplaySettingsEditor? _display;
        private readonly ComboBox _security = new() { Header = "Connection security", HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly CheckBox _clipboard = new() { Content = "Allow text clipboard redirection (opt-in)" };
        public Control[] Controls { get; }
        public AdditionalFields(ConnectionProfile profile)
        {
            _name.Text = profile.Name; _group.Text = profile.Group; _user.Text = profile.UserName; _domain.Text = profile.Domain;
            _pin.Text = profile.CertificateSha256 ?? ""; _port.Value = profile.Port;
            _security.Items.Add("Network Level Authentication (recommended)");
            _security.Items.Add("TLS (only for hosts explicitly configured without NLA)");
            _security.SelectedIndex = (int)profile.Security;
            _clipboard.IsChecked = profile.RedirectClipboard;
            Controls = [new SettingsSectionTabs(
                ("General", () =>
                {
                    var panel = new StackPanel { Spacing = 12 };
                    foreach (var field in new Control[] { _name, _port, _group, _user, _domain, _security, _pin, _clipboard }) panel.Children.Add(field);
                    return panel;
                }),
                ("Display", () =>
                {
                    _display = new SavedDisplaySettingsEditor(profile);
                    var panel = new StackPanel(); panel.Children.Add(_display); return panel;
                }))];
        }
        public ConnectionProfile Read(ConnectionProfile original)
        {
            var result = original with
            {
            Name = _name.Text, Group = _group.Text.Trim(), UserName = _user.Text.Trim(), Domain = _domain.Text.Trim(),
            Port = Integer(_port.Value, "port"), Security = (SecurityMode)_security.SelectedIndex,
            CertificateSha256 = string.IsNullOrWhiteSpace(_pin.Text) ? null : _pin.Text.Trim().Replace(":", ""), RedirectClipboard = _clipboard.IsChecked == true
            };
            return _display?.Read(result) ?? result;
        }
    }

    private static int Integer(double value, string field) => double.IsFinite(value) && value == Math.Truncate(value) && value <= int.MaxValue && value >= int.MinValue ? (int)value : throw new ArgumentException("Enter a whole number for " + field + ".");
    private static TextBox Field(string label) { var box = new TextBox { Header = label }; AutomationProperties.SetName(box, label); return box; }
}
