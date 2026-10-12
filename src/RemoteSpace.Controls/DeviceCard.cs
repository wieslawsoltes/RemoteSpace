using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Profiles;

namespace RemoteSpace.Controls;

public sealed class DeviceActionEventArgs(ConnectionProfile profile, string action) : EventArgs
{
    public ConnectionProfile Profile { get; } = profile;
    public string Action { get; } = action;
}

/// <summary>One reusable visual tree for tiles and rows. Rebinding a recycled device
/// updates values, never rebuilds controls. No connection or storage is owned here.</summary>
public sealed partial class DeviceCard : UserControl
{
    public static readonly DependencyProperty ProfileProperty = DependencyProperty.Register(nameof(Profile), typeof(ConnectionProfile), typeof(DeviceCard), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty LastConnectedProperty = DependencyProperty.Register(nameof(LastConnected), typeof(DateTimeOffset?), typeof(DeviceCard), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty IsListProperty = DependencyProperty.Register(nameof(IsList), typeof(bool), typeof(DeviceCard), new PropertyMetadata(false, Changed));
    public ConnectionProfile? Profile { get => (ConnectionProfile?)GetValue(ProfileProperty); set => SetValue(ProfileProperty, value); }
    public DateTimeOffset? LastConnected { get => (DateTimeOffset?)GetValue(LastConnectedProperty); set => SetValue(LastConnectedProperty, value); }
    public bool IsList { get => (bool)GetValue(IsListProperty); set => SetValue(IsListProperty, value); }
    public event EventHandler<DeviceActionEventArgs>? ActionRequested;
    private bool _openingMenu;
    private MenuFlyout? _activeMenu;
    public DeviceCard()
    {
        InitializeComponent(); HorizontalAlignment = HorizontalAlignment.Stretch;
        Unloaded += (_, _) => _activeMenu?.Hide();
        Update();
    }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs _) => ((DeviceCard)sender).Update();
    private void Update()
    {
        if (DeviceName is null) return;
        var p = Profile;
        DeviceName.Text = p?.Name ?? "Remote PC";
        DeviceAddress.Text = p is null ? "" : (p.Host.Contains(':') ? $"[{p.Host}]:{p.Port}" : $"{p.Host}:{p.Port}") + " · " + p.Group;
        Recency.Text = LastConnected is { } time ? "Last connected " + time.ToLocalTime().ToString("g") : "Not connected yet";
        FavoriteIcon.Glyph = p?.Favorite == true ? "\uE735" : "\uE734";
        Artwork.Variant = p is null ? 0 : (int)((uint)p.Id.GetHashCode() % 3);
        AutomationProperties.SetName(ConnectButton, "Connect to " + p?.Name);
        AutomationProperties.SetName(PreviewButton, "Open " + p?.Name);
        AutomationProperties.SetName(FavoriteButton, "Toggle favorite " + p?.Name);
        AutomationProperties.SetName(MoreButton, "Options for " + p?.Name);
        ToolTipService.SetToolTip(DeviceName, p?.Name);
        ToolTipService.SetToolTip(DeviceAddress, DeviceAddress.Text);
        ToolTipService.SetToolTip(FavoriteButton, p?.Favorite == true ? "Remove from favorites" : "Add to favorites");
        Grid.SetColumnSpan(Preview, IsList ? 1 : 2);
        Grid.SetRow(Details, IsList ? 0 : 1); Grid.SetColumn(Details, IsList ? 1 : 0); Grid.SetColumnSpan(Details, IsList ? 1 : 2);
        Preview.Width = IsList ? 100 : double.NaN; Preview.Height = IsList ? 120 : 132;
        TypeBadge.Visibility = IsList ? Visibility.Collapsed : Visibility.Visible;
        Details.Padding = IsList ? new Thickness(12, 10, 12, 10) : new Thickness(16, 12, 16, 14);
    }
    private void ConnectClicked(object sender, RoutedEventArgs e) => Request("connect");
    private void FavoriteClicked(object sender, RoutedEventArgs e) => Request("favorite");
    private async void MoreClicked(object sender, RoutedEventArgs e)
    {
        if (_openingMenu) return;
        _openingMenu = true;
        try
        {
            // Keep the initiating keyup on its button before focus enters the
            // flyout. Otherwise a later dialog can restore a still-pressed button.
            await CommandActivation.WaitForReleaseAsync(this);
            _activeMenu?.Hide();
            // Allocate menus only when requested, not for every virtualized item.
            var menu = new MenuFlyout();
            foreach (var (label, action) in new[] { ("Connect", "connect"), ("Edit", "edit"), ("Duplicate", "duplicate"), ("View details", "details"),
                         (Profile?.Favorite == true ? "Remove from favorites" : "Add to favorites", "favorite"), ("Export .rdp", "export"), ("Delete", "delete") })
            {
                var item = new MenuFlyoutItem { Text = label }; item.Click += (_, _) => Request(action); menu.Items.Add(item);
            }
            _activeMenu = menu;
            menu.Closed += (_, _) => { if (ReferenceEquals(_activeMenu, menu)) _activeMenu = null; };
            AutomationProperties.SetHelpText(MoreButton, "Open device commands");
            ToolTipService.SetToolTip(MoreButton, "Open device commands");
            menu.ShowAt(MoreButton);
        }
        catch (OperationCanceledException) when (!IsLoaded) { }
        catch (Exception error)
        {
            AutomationProperties.SetHelpText(MoreButton, error.Message);
            ToolTipService.SetToolTip(MoreButton, error.Message);
        }
        finally { _openingMenu = false; }
    }
    private void Request(string action) { if (Profile is { } profile) ActionRequested?.Invoke(this, new(profile, action)); }
}
