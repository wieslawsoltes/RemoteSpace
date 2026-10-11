using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Controls;
using RemoteSpace.Core;
using RemoteSpace.Profiles;
using RemoteSpace.Transport;
using RemoteSpace.Workspace;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;

namespace RemoteSpace.App;

public sealed partial class MainPage : Page
{
    private readonly IUserFileAccess _fileAccess = new SystemFileAccess();
    private DeviceCatalog _catalog = new();
    private readonly WorkspaceStore _store = new();
    private readonly List<OpenSession> _sessions = [];
    private readonly SemaphoreSlim _mutations = new(1);
    private bool _ready, _loading, _dialogOpen;
    private string _section = "devices";
    private readonly Queue<string> _diagnostics = new();
    public MainPage()
    {
        InitializeComponent();
        InitializeWorkspaceAccessibility();
        Devices.Loaded += (_, _) => UpdateTileSize();
        DeviceGridScroll.SizeChanged += (_, _) => UpdateTileSize();
        Loaded += async (_, _) =>
        {
            if (_ready || _loading) return;
            _loading = true;
            try { await _textClipboard.InitializeAsync(); }
            catch (Exception e) { ShowError("Local clipboard unavailable: " + e.Message); }
            try { await _fileAccess.InitializeAsync(); }
            catch (Exception e) { ShowError("Local file access unavailable: " + e.Message); }
            try { _catalog = await _store.LoadAsync(); }
            catch (Exception e) { ShowError("Workspace not loaded: " + e.Message); }
            _ready = true; _loading = false; ApplyTheme(_catalog.Settings.Theme); RefreshDevices();
            Status.Text = OperatingSystem.IsBrowser() ? "Preview · Browser sessions require your trusted bridge" : "Preview · Direct RDP or trusted bridge";
            Console.WriteLine("REMOTESPACE_READY");
        };
    }
    private void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (DevicePanel is null) return;
        _section = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "devices";
        Heading.Text = _section switch { "favorites" => "Favorites", "sessions" => "Active sessions", "settings" => "Settings", "apps" => "Apps", _ => "Devices" };
        DevicePanel.Visibility = _section is "devices" or "favorites" ? Visibility.Visible : Visibility.Collapsed;
        Sessions.Visibility = _section == "sessions" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = _section == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AppsPanel.Visibility = _section == "apps" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var session in _sessions) session.View.ReleaseHeldInput();
        if (_section == "settings") BuildSettings();
        RefreshSessionState(); RefreshDevices();
    }
    private async void NavigationInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if ((args.InvokedItemContainer as NavigationViewItem)?.Tag?.ToString() == "add") await EditProfile(null);
    }
    private void RefreshSessionState()
    {
        if (SessionsEmpty is null) return;
        SessionsEmpty.Visibility = _section == "sessions" && _sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionCount.Text = _sessions.Count == 0 ? "Sessions" : $"Sessions ({_sessions.Count})";
    }
    private void WorkspaceSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (DeviceToolbar is null) return;
        bool narrow = args.NewSize.Width < 650;
        WorkspaceContent.Padding = new Thickness(narrow ? 12 : 28, 24, narrow ? 12 : 28, 12);
        Grid.SetRow(DeviceCommands, narrow ? 1 : 0); Grid.SetColumn(DeviceCommands, narrow ? 0 : 1);
        Grid.SetColumnSpan(DeviceCommands, narrow ? 2 : 1); Grid.SetColumnSpan(Search, narrow ? 2 : 1);
        DeviceCommands.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        Search.Width = Math.Max(180, Math.Min(360, args.NewSize.Width - WorkspaceContent.Padding.Left - WorkspaceContent.Padding.Right - (narrow ? 0 : 292)));
        UpdateTileSize();
    }
    private void UpdateTileSize()
    {
        // ItemsWrapGrid is not implemented by the pinned Uno Skia backend.
        // UniformGridLayout keeps finite viewport-based realization instead of
        // falling back to GridView's nonvirtualizing WrapPanel.
        if (Devices?.Layout is UniformGridLayout layout && DeviceGridScroll is not null)
        {
            double available = DeviceGridScroll.ActualWidth > 0 ? DeviceGridScroll.ActualWidth :
                WorkspaceContent.ActualWidth - WorkspaceContent.Padding.Left - WorkspaceContent.Padding.Right;
            double width = Math.Max(180, Math.Min(300, available - 16));
            if (layout.MinItemWidth != width) layout.MinItemWidth = width;
        }
    }
    private void GoToDevices(object sender, RoutedEventArgs args) => Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(i => i.Tag?.ToString() == "devices");
    private async void AboutClicked(object sender, RoutedEventArgs args) => await ShowDialog("About RemoteSpace", new TextBlock
    {
        Text = "Independent RDP client · Experimental preview\n\nReusable .NET components, Uno Platform and Skia rendering. The local-PC workspace follows Windows App navigation and device-card conventions.\n\nCloud accounts, RemoteApp, RD Gateway, modern GFX codecs and device redirection are not yet implemented. This is not Microsoft Windows App and is not a security-reviewed replacement. Connection passwords and bridge tokens are never saved.",
        TextWrapping = TextWrapping.Wrap, MaxWidth = 480
    });
    private async Task ShowDialog(string title, object content)
    {
        if (_dialogOpen) return; _dialogOpen = true;
        try { await new AccessibleInfoDialog(title, content) { XamlRoot = XamlRoot }.ShowAsync(); }
        catch (Exception e) { ShowError(e.Message); }
        finally { _dialogOpen = false; }
    }
    private async Task<bool> Confirm(string title, string message, string action)
    {
        if (_dialogOpen) return false; _dialogOpen = true;
        try { return await new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, PrimaryButtonText = action, CloseButtonText = "Cancel" }.ShowAsync() == ContentDialogResult.Primary; }
        finally { _dialogOpen = false; }
    }
    private void ApplyTheme(WorkspaceTheme theme) => RequestedTheme = theme switch { WorkspaceTheme.Light => ElementTheme.Light, WorkspaceTheme.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
    private void ShowError(string message) => ShowMessage("RemoteSpace", message, InfoBarSeverity.Error);
    private void ShowMessage(string title, string message, InfoBarSeverity severity) { Notice.Title = title; Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true; }
}
