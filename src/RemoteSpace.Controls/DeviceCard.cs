using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RemoteSpace.Profiles;

namespace RemoteSpace.Controls;

public sealed class DeviceActionEventArgs(ConnectionProfile profile, string action) : EventArgs
{
    public ConnectionProfile Profile { get; } = profile;
    public string Action { get; } = action;
}

/// <summary>Reusable device tile with accessible commands; it does not connect or persist data itself.</summary>
public sealed class DeviceCard : UserControl
{
    public static readonly DependencyProperty ProfileProperty = DependencyProperty.Register(nameof(Profile), typeof(ConnectionProfile), typeof(DeviceCard), new PropertyMetadata(null, Changed));
    public ConnectionProfile? Profile { get => (ConnectionProfile?)GetValue(ProfileProperty); set => SetValue(ProfileProperty, value); }
    public event EventHandler<DeviceActionEventArgs>? ActionRequested;
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs _) => ((DeviceCard)sender).Rebuild();
    public DeviceCard() { MinWidth = 250; HorizontalAlignment = HorizontalAlignment.Stretch; Rebuild(); }
    private void Rebuild()
    {
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(16), MinWidth = 248 };
        var illustration = new Border { Height = 112, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 225, 237, 250)) };
        illustration.Child = new FontIcon { Glyph = "\uE7F4", FontSize = 52, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 30, 99, 181)) };
        panel.Children.Add(illustration);
        panel.Children.Add(new TextBlock { Text = Profile?.Name ?? "PC", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = Profile is { } p ? $"{p.Host}:{p.Port}" : "", Opacity = .7, TextTrimming = TextTrimming.CharacterEllipsis });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var connect = new Button { Content = "Connect", MinWidth = 118, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(connect, "Connect to " + Profile?.Name);
        connect.Click += (_, _) => Request("connect"); row.Children.Add(connect);
        var favorite = new Button { Content = Profile?.Favorite == true ? "★" : "☆", Padding = new Thickness(10, 5, 10, 5) };
        AutomationProperties.SetName(favorite, "Toggle favorite " + Profile?.Name);
        favorite.Click += (_, _) => Request("favorite"); row.Children.Add(favorite);
        var menu = new MenuFlyout();
        foreach (var (text, action) in new[] { ("Edit", "edit"), ("Export .rdp", "export"), ("Delete", "delete") })
        {
            var item = new MenuFlyoutItem { Text = text }; item.Click += (_, _) => Request(action); menu.Items.Add(item);
        }
        var more = new Button { Content = "⋯", Flyout = menu }; AutomationProperties.SetName(more, "Options for " + Profile?.Name); row.Children.Add(more);
        panel.Children.Add(row);
        Content = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], Child = panel };
    }
    private void Request(string action) { if (Profile is { } profile) ActionRequested?.Invoke(this, new(profile, action)); }
}
