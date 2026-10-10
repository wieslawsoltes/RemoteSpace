using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Controls;
using RemoteSpace.Core;
using RemoteSpace.Profiles;
using RemoteSpace.Transport;
using RemoteSpace.Workspace;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;

namespace RemoteSpace.App;

public sealed partial class MainPage : Page
{
    private DeviceCatalog _catalog = new();
    private readonly WorkspaceStore _store = new();
    private readonly List<OpenSession> _sessions = [];
    private readonly SemaphoreSlim _mutations = new(1);
    private bool _ready, _dialogOpen;
    private string _section = "devices";
    private readonly Queue<string> _diagnostics = new();
    public MainPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_ready) return;
            try { _catalog = await _store.LoadAsync(); }
            catch (Exception e) { ShowError("Workspace not loaded: " + e.Message); }
            var settings = _catalog.Snapshot().Settings;
            Sort.SelectedIndex = (int)settings.Sort; ListMode.IsOn = settings.ListView; ApplyTheme(settings.Theme);
            _ready = true; RefreshDevices();
            Status.Text = OperatingSystem.IsBrowser() ? "Browser · Uno Skia · Live sessions require your authenticated bridge" : "Desktop · Uno Skia · Direct RDP or authenticated bridge";
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
        if (_section == "settings") BuildSettings(); else RefreshDevices();
    }
    private void SearchChanged(object sender, TextChangedEventArgs args) { if (_ready) RefreshDevices(); }
    private async void SortChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_ready || Sort.SelectedIndex < 0) return;
        await Mutate(() => _catalog.Configure(_catalog.Snapshot().Settings with { Sort = (ProfileSort)Sort.SelectedIndex }));
    }
    private async void ListModeChanged(object sender, RoutedEventArgs args)
    {
        if (_ready) await Mutate(() => _catalog.Configure(_catalog.Snapshot().Settings with { ListView = ListMode.IsOn }));
    }
    private void RefreshDevices()
    {
        if (Devices is null) return;
        var items = _catalog.Query(Search.Text ?? "", _section == "favorites"); Devices.ItemsSource = items; DeviceList.ItemsSource = items;
        DeviceCount.Text = $"{items.Count} saved PC{(items.Count == 1 ? "" : "s")}";
        bool list = _catalog.Snapshot().Settings.ListView;
        Devices.Visibility = list ? Visibility.Collapsed : Visibility.Visible; DeviceList.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task Mutate(Action mutation)
    {
        await _mutations.WaitAsync(); var previous = _catalog.Serialize();
        try { mutation(); await _store.SaveAsync(_catalog); }
        catch (Exception e) { _catalog = DeviceCatalog.Parse(previous); ShowError(e.Message); }
        finally { RefreshDevices(); _mutations.Release(); }
    }
    private async void AddPc(object sender, RoutedEventArgs args) => await EditProfile(null);
    private async Task EditProfile(ConnectionProfile? original)
    {
        if (_dialogOpen) return; _dialogOpen = true;
        try
        {
            var editor = new ProfileEditor(original); var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel { Spacing = 12 }; content.Children.Add(error); content.Children.Add(editor);
            var dialog = new ContentDialog { Title = original is null ? "Add a PC" : "Edit PC", Content = content, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
            ConnectionProfile? result = null;
            dialog.PrimaryButtonClick += (_, e) => { try { result = editor.ReadProfile(); } catch (Exception ex) { e.Cancel = true; error.Text = ex.Message; } };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && result is not null) await Mutate(() => _catalog.Upsert(result));
        }
        catch (Exception e) { ShowError(e.Message); }
        finally { _dialogOpen = false; }
    }
    private async void DeviceAction(object sender, DeviceActionEventArgs args)
    {
        switch (args.Action)
        {
            case "connect": await Connect(args.Profile); break;
            case "edit": await EditProfile(args.Profile); break;
            case "favorite": await Mutate(() => _catalog.Upsert(args.Profile with { Favorite = !args.Profile.Favorite })); break;
            case "export": await ExportProfile(args.Profile); break;
            case "delete":
                if (await Confirm("Remove PC", "Remove " + args.Profile.Name + " from saved devices? Active sessions will stay open.", "Remove")) await Mutate(() => _catalog.Remove(args.Profile.Id));
                break;
        }
    }
    private async void DeviceListClicked(object sender, ItemClickEventArgs args) { if (args.ClickedItem is SavedDevice device) await Connect(device.Profile); }
    private async void ImportRdp(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".rdp");
            var file = await picker.PickSingleFileAsync(); if (file is null) return;
            var properties = await file.GetBasicPropertiesAsync(); if (properties.Size > 1024 * 1024) throw new IOException("The RDP file exceeds 1 MiB.");
            using var stream = await file.OpenStreamForReadAsync(); using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer);
            var imported = RdpFile.Parse(buffer.ToArray());
            await Mutate(() => _catalog.Upsert(imported.Profile));
            if (imported.Warnings.Count > 0) ShowMessage("RDP import", string.Join("\n", imported.Warnings), InfoBarSeverity.Warning);
        }
        catch (Exception e) { ShowError(e.Message); }
    }
    private async Task ExportProfile(ConnectionProfile profile)
    {
        try { await SaveText("Export PC", profile.Name, ".rdp", RdpFile.Export(profile)); }
        catch (Exception e) { ShowError(e.Message); }
    }
    private async Task SaveText(string title, string name, string extension, string text)
    {
        var picker = new FileSavePicker { SuggestedFileName = name }; picker.FileTypeChoices.Add(title, new List<string> { extension });
        var file = await picker.PickSaveFileAsync(); if (file is not null) await FileIO.WriteTextAsync(file, text);
    }
    private async Task Connect(ConnectionProfile profile)
    {
        if (_dialogOpen) return;
        if (_sessions.Count >= 8) { ShowError("Close a session before opening another. This preview limits each app window to eight sessions."); return; }
        _dialogOpen = true;
        try
        {
            var bridge = _catalog.Snapshot().Settings.BridgeEndpoint;
            if (OperatingSystem.IsBrowser() && string.IsNullOrEmpty(bridge)) { ShowError("Set your authenticated wss:// bridge endpoint in Settings before connecting from the browser."); return; }
            var user = new TextBox { Header = "User name", Text = profile.UserName };
            var domain = new TextBox { Header = "Domain", Text = profile.Domain };
            var password = new PasswordBox { Header = "Password" }; AutomationProperties.SetName(password, "Connection password");
            var token = new PasswordBox { Header = "Bridge access token" }; AutomationProperties.SetName(token, "Bridge access token");
            var fields = new StackPanel { Spacing = 14, MinWidth = 350 };
            fields.Children.Add(new TextBlock { Text = $"{profile.Host}:{profile.Port} · {profile.Security}", TextWrapping = TextWrapping.Wrap });
            fields.Children.Add(user); fields.Children.Add(domain); fields.Children.Add(password);
            if (bridge.Length > 0) { fields.Children.Add(new TextBlock { Text = bridge, TextWrapping = TextWrapping.Wrap }); fields.Children.Add(token); }
            fields.Children.Add(new TextBlock { Text = "Credentials are kept only for this connection. The bridge, when used, terminates the RDP connection and must be operated by someone you trust.", TextWrapping = TextWrapping.Wrap, MaxWidth = 430, Opacity = .7 });
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Connect to " + profile.Name, PrimaryButtonText = "Connect", CloseButtonText = "Cancel", Content = fields };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) { password.Password = token.Password = ""; return; }
            IRemoteSession session;
            if (bridge.Length > 0) session = new BridgeSession(new Uri(bridge), token.Password, "https://remotespace.local");
#if __WASM__
            else throw new InvalidOperationException("A browser connection requires the authenticated bridge.");
#else
            else session = new RemoteSpace.Client.RdpSession();
#endif
            var credentials = new SessionCredentials(user.Text, password.Password, domain.Text); password.Password = token.Password = "";
            var view = new RemoteDesktopView(); await view.AttachAsync(session);
            var state = new TextBlock { Text = "Connecting…", VerticalAlignment = VerticalAlignment.Center };
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(8) };
            var panel = new Grid(); panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            panel.Children.Add(toolbar); Grid.SetRow(view, 1); panel.Children.Add(view);
            var tab = new TabViewItem { Header = profile.Name, Content = panel, IsClosable = true };
            var opened = new OpenSession(profile, session, view, tab); _sessions.Add(opened);
            void Button(string label, Action action) { var b = new Button { Content = label }; b.Click += (_, _) => action(); toolbar.Children.Add(b); }
            Button("Fit", () => { view.FitToWindow = true; view.Focus(FocusState.Programmatic); });
            Button("100%", () => { view.Zoom = 1; view.FitToWindow = false; });
            Button("−", () => { view.FitToWindow = false; view.Zoom = Math.Max(.1, view.Zoom - .25); });
            Button("+", () => { view.FitToWindow = false; view.Zoom = Math.Min(4, view.Zoom + .25); });
            Button("Full screen", () => { var appView = ApplicationView.GetForCurrentView(); if (appView.IsFullScreenMode) appView.ExitFullScreenMode(); else appView.TryEnterFullScreenMode(); });
            Button("Ctrl+Alt+Del", () => view.SendSecureAttention());
            var paste = new Button { Content = "Send clipboard", IsEnabled = profile.RedirectClipboard }; paste.Click += async (_, _) =>
            {
                try { var data = Clipboard.GetContent(); if (data.Contains(StandardDataFormats.Text)) await session.SendClipboardAsync(await data.GetTextAsync()); }
                catch (Exception e) { ShowError(e.Message); }
            }; toolbar.Children.Add(paste);
            var copy = new Button { Content = "Copy remote text", IsEnabled = profile.RedirectClipboard }; copy.Click += (_, _) =>
            {
                try { var data = new DataPackage(); data.SetText(opened.RemoteClipboard); Clipboard.SetContent(data); }
                catch (Exception e) { ShowError(e.Message); }
            }; toolbar.Children.Add(copy);
            var capture = new Button { Content = "Capture" }; capture.Click += async (_, _) =>
            {
                try
                {
                    var png = view.CapturePng(); var picker = new FileSavePicker { SuggestedFileName = profile.Name + "-desktop" }; picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
                    var file = await picker.PickSaveFileAsync(); if (file is not null) await FileIO.WriteBytesAsync(file, png);
                }
                catch (Exception e) { ShowError(e.Message); }
            }; toolbar.Children.Add(capture); toolbar.Children.Add(state);
            view.InputError += ShowError;
            session.Diagnostic += d => DispatcherQueue.TryEnqueue(() => { while (_diagnostics.Count >= 200) _diagnostics.Dequeue(); _diagnostics.Enqueue($"{d.Time:O} [{d.Stage}] {d.Message}"); });
            session.ClipboardReceived += text => DispatcherQueue.TryEnqueue(() => opened.RemoteClipboard = text);
            session.StateChanged += s => DispatcherQueue.TryEnqueue(() =>
            {
                state.Text = s.ToString(); tab.Header = profile.Name + " · " + s;
                if (s == SessionState.Active && !opened.RecordedActive) { opened.RecordedActive = true; _ = Mutate(() => _catalog.MarkConnected(profile.Id, DateTimeOffset.UtcNow)); view.Focus(FocusState.Programmatic); }
                if (s != SessionState.Active) view.ReleaseHeldInput();
            });
            Sessions.TabItems.Add(tab); Sessions.SelectedItem = tab;
            Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == "sessions");
            opened.Running = RunSession(opened, profile.ToOptions(), credentials);
        }
        catch (Exception e) { ShowError(e.Message); }
        finally { _dialogOpen = false; }
    }
    private async Task RunSession(OpenSession opened, ConnectionOptions options, SessionCredentials credentials)
    {
        try { await opened.Session.RunAsync(options, credentials, opened.Stop.Token); }
        catch (OperationCanceledException) when (opened.Stop.IsCancellationRequested) { }
        catch (Exception e) { DispatcherQueue.TryEnqueue(() => ShowError(e.Message)); }
    }
    private async void CloseSession(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        var opened = _sessions.FirstOrDefault(s => s.Tab == args.Tab); if (opened is null || opened.Closing) return;
        if (opened.Session.State == SessionState.Active && !await Confirm("Disconnect session", "Disconnect from " + opened.Profile.Name + "? Programs running on the remote PC are not closed.", "Disconnect")) return;
        opened.Closing = true;
        try
        {
            await opened.View.AttachAsync(null); opened.Stop.Cancel(); await opened.Session.DisconnectAsync(); await opened.Running;
            await opened.View.DisposeAsync(); await opened.Session.DisposeAsync(); opened.Stop.Dispose();
            _sessions.Remove(opened); Sessions.TabItems.Remove(opened.Tab);
        }
        catch (Exception e) { opened.Closing = false; ShowError(e.Message); }
    }
    private void SessionSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        foreach (var opened in _sessions) if (Sessions.SelectedItem != opened.Tab) opened.View.ReleaseHeldInput();
    }
    private async Task<bool> Confirm(string title, string message, string action)
    {
        if (_dialogOpen) return false; _dialogOpen = true;
        try { return await new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, PrimaryButtonText = action, CloseButtonText = "Cancel" }.ShowAsync() == ContentDialogResult.Primary; }
        finally { _dialogOpen = false; }
    }
    private void BuildSettings()
    {
        SettingsBody.Children.Clear(); var settings = _catalog.Snapshot().Settings;
        SettingsBody.Children.Add(new TextBlock { Text = "Appearance", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 0) });
        var theme = new ComboBox { Header = "App theme", ItemsSource = Enum.GetNames<WorkspaceTheme>(), SelectedIndex = (int)settings.Theme, MinWidth = 240 };
        theme.SelectionChanged += async (_, _) => { await Mutate(() => _catalog.Configure(_catalog.Snapshot().Settings with { Theme = (WorkspaceTheme)theme.SelectedIndex })); ApplyTheme(_catalog.Snapshot().Settings.Theme); }; SettingsBody.Children.Add(theme);
        SettingsBody.Children.Add(new TextBlock { Text = "Browser connection bridge", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var endpoint = new TextBox { Header = "Bridge endpoint", PlaceholderText = "wss://your-bridge.example/session", Text = settings.BridgeEndpoint, MinWidth = 440 };
        AutomationProperties.SetName(endpoint, "Bridge endpoint"); SettingsBody.Children.Add(endpoint);
        SettingsBody.Children.Add(new TextBlock { Text = "The static app cannot open TCP connections. Use the included bridge on a trusted machine and allow only your own RDP targets and this app's origin. Leave this blank on desktop to connect directly. Tokens are requested per connection, never stored in settings or URLs.", TextWrapping = TextWrapping.Wrap, Opacity = .7 });
        var save = new Button { Content = "Save bridge endpoint" }; save.Click += async (_, _) => await Mutate(() => _catalog.Configure(_catalog.Snapshot().Settings with { BridgeEndpoint = endpoint.Text.Trim() })); SettingsBody.Children.Add(save);
        var export = new Button { Content = "Export saved devices" }; export.Click += async (_, _) => { try { await SaveText("RemoteSpace workspace", "RemoteSpace-workspace", ".json", _catalog.Serialize()); } catch (Exception e) { ShowError(e.Message); } }; SettingsBody.Children.Add(export);
        var import = new Button { Content = "Import saved devices" }; import.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json"); var file = await picker.PickSingleFileAsync(); if (file is null) return;
                if ((await file.GetBasicPropertiesAsync()).Size > DeviceCatalog.MaximumDocumentBytes) throw new IOException("Workspace import exceeds the size limit.");
                var devices = DeviceCatalog.Parse(await FileIO.ReadTextAsync(file)).Snapshot().Devices;
                if (await Confirm("Import devices", $"Import {devices.Length} saved PCs? Existing IDs will be updated. Connection secrets are not part of this file.", "Import")) await Mutate(() => { foreach (var device in devices) _catalog.Upsert(device.Profile); });
            }
            catch (Exception e) { ShowError(e.Message); }
        }; SettingsBody.Children.Add(import);
        SettingsBody.Children.Add(new TextBlock { Text = "Diagnostics", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var diagnostics = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 250, Text = _diagnostics.Count == 0 ? "No session diagnostics yet." : string.Join("\n", _diagnostics) }; SettingsBody.Children.Add(diagnostics);
        SettingsBody.Children.Add(new TextBlock { Text = "Implemented preview: direct RDP/NLA/TLS engine, bitmap updates, input, text clipboard, credential-free device management and multi-session workspace. Not full Windows App parity. Cloud subscriptions, RemoteApp, RD Gateway, modern GFX codecs, audio and device redirection are not implemented.", TextWrapping = TextWrapping.Wrap, Opacity = .7 });
    }
    private void ApplyTheme(WorkspaceTheme theme) => RequestedTheme = theme switch { WorkspaceTheme.Light => ElementTheme.Light, WorkspaceTheme.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
    private void ShowError(string message) => ShowMessage("RemoteSpace", message, InfoBarSeverity.Error);
    private void ShowMessage(string title, string message, InfoBarSeverity severity) { Notice.Title = title; Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true; }
    private sealed class OpenSession(ConnectionProfile profile, IRemoteSession session, RemoteDesktopView view, TabViewItem tab)
    {
        public ConnectionProfile Profile { get; } = profile;
        public IRemoteSession Session { get; } = session;
        public RemoteDesktopView View { get; } = view;
        public TabViewItem Tab { get; } = tab;
        public CancellationTokenSource Stop { get; } = new();
        public Task Running { get; set; } = Task.CompletedTask;
        public string RemoteClipboard { get; set; } = "";
        public bool RecordedActive { get; set; }
        public bool Closing { get; set; }
    }
}
