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

public sealed partial class MainPage
{
    private async Task Connect(ConnectionProfile profile)
    {
        if (_dialogOpen) return;
        if (_sessions.Count >= 8) { ShowError("Close a session before opening another. This preview limits each app window to eight sessions."); return; }
        _dialogOpen = true;
        try
        {
            var bridge = _catalog.Settings.BridgeEndpoint;
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
            panel.Children.Add(new ScrollViewer { Content = toolbar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }); Grid.SetRow(view, 1); panel.Children.Add(view);
            var tab = new TabViewItem { Header = profile.Name, Content = panel, IsClosable = true };
            var opened = new OpenSession(profile, session, view, tab); _sessions.Add(opened);
            void Button(string label, Action action) { var b = new Button { Content = label }; b.Click += (_, _) => action(); toolbar.Children.Add(b); }
            AddDisplayControls(session, view, toolbar);
            Button("Fit", () => { view.FitToWindow = true; view.Focus(FocusState.Programmatic); });
            Button("100%", () => { view.Zoom = 1; view.FitToWindow = false; });
            Button("−", () => { view.FitToWindow = false; view.Zoom = Math.Max(.1, view.Zoom - .25); });
            Button("+", () => { view.FitToWindow = false; view.Zoom = Math.Min(4, view.Zoom + .25); });
            Button("Full screen", () => { var appView = ApplicationView.GetForCurrentView(); if (appView.IsFullScreenMode) appView.ExitFullScreenMode(); else appView.TryEnterFullScreenMode(); });
            Button("Ctrl+Alt+Del", () => view.SendSecureAttention());
            AddClipboardCommands(toolbar, opened);
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
                if (s == SessionState.Active && !opened.RecordedActive) { opened.RecordedActive = true; _ = Mutate(c => { if (c.Find(profile.Id) is not null) c.MarkConnected(profile.Id, DateTimeOffset.UtcNow); }); view.Focus(FocusState.Programmatic); }
                if (s != SessionState.Active) view.ReleaseHeldInput();
            });
            Sessions.TabItems.Add(tab); Sessions.SelectedItem = tab; RefreshSessionState();
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
            _sessions.Remove(opened); Sessions.TabItems.Remove(opened.Tab); RefreshSessionState();
        }
        catch (Exception e) { opened.Closing = false; ShowError(e.Message); }
    }
    private void SessionSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        foreach (var opened in _sessions) if (Sessions.SelectedItem != opened.Tab) opened.View.ReleaseHeldInput();
    }
    private sealed class OpenSession(ConnectionProfile profile, IRemoteSession session, RemoteDesktopView view, TabViewItem tab)
    {
        public ConnectionProfile Profile { get; } = profile;
        public IRemoteSession Session { get; } = session;
        public RemoteDesktopView View { get; } = view;
        public TabViewItem Tab { get; } = tab;
        public CancellationTokenSource Stop { get; } = new();
        public Task Running { get; set; } = Task.CompletedTask;
        public string? RemoteClipboard { get; set; }
        public bool RecordedActive { get; set; }
        public bool Closing { get; set; }
    }
}
