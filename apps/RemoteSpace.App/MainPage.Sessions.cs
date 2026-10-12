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
        PasswordBox? password = null, token = null;
        IRemoteSession? pendingSession = null;
        RemoteDesktopView? pendingView = null;
        OpenSession? pendingTab = null;
        try
        {
            var bridge = _catalog.Settings.BridgeEndpoint;
            if (OperatingSystem.IsBrowser() && string.IsNullOrEmpty(bridge)) { ShowError("Set your authenticated wss:// bridge endpoint in Settings before connecting from the browser."); return; }
            var user = new TextBox { Header = "User name", Text = profile.UserName };
            var domain = new TextBox { Header = "Domain", Text = profile.Domain };
            password = new PasswordBox { Header = "Password" }; AutomationProperties.SetName(password, "Connection password");
            token = new PasswordBox { Header = "Bridge access token" }; AutomationProperties.SetName(token, "Bridge access token");
            var fields = new StackPanel { Spacing = 14, MinWidth = 250 };
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
            pendingSession = session;
            var credentials = new SessionCredentials(user.Text, password.Password, domain.Text); password.Password = token.Password = "";
            EnsureSessionPresentation();
            var view = new RemoteDesktopView(); pendingView = view; await view.AttachAsync(session);
            var toolbar = new SessionCommandBar { Title = profile.Name, Status = "Connecting…" };
            toolbar.CommandFailed += ShowError;
            var panel = new Grid(); panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            panel.Children.Add(toolbar); Grid.SetRow(view, 1); panel.Children.Add(view);
            var tab = new TabViewItem { Header = profile.Name, Content = panel, IsClosable = true };
            var opened = new OpenSession(profile, session, view, tab); pendingTab = opened;
            toolbar.AddCommand("Fit", "\uE9A6", () => { view.SetScale(null); view.Focus(FocusState.Programmatic); return Task.CompletedTask; });
            AddDisplayControls(session, view, toolbar);
            AddClipboardCommands(toolbar, opened);
            AddSessionActions(toolbar, opened);
            toolbar.AddCommand("Full screen", "\uE740", () =>
            {
                var appView = ApplicationView.GetForCurrentView();
                if (appView.IsFullScreenMode) appView.ExitFullScreenMode();
                else if (!appView.TryEnterFullScreenMode()) throw new InvalidOperationException("Full screen was declined by the host. Try the browser's full-screen command.");
                return Task.CompletedTask;
            });
            void UpdateBar() => toolbar.Status = $"{session.State} · {view.DesktopWidth} × {view.DesktopHeight} · {(view.FitToWindow ? "Fit" : view.Zoom.ToString("0%"))}";
            view.ViewChanged += UpdateBar;
            if (session is IRemoteGraphicsOutput output)
                output.OutputEnabledChanged += enabled => DispatcherQueue.TryEnqueue(() => { if (_sessions.Contains(opened)) view.IsEnabled = enabled; });
            view.InputError += ShowError;
            session.Diagnostic += d => DispatcherQueue.TryEnqueue(() => { while (_diagnostics.Count >= 200) _diagnostics.Dequeue(); _diagnostics.Enqueue($"{d.Time:O} [{d.Stage}] {d.Message}"); });
            session.ClipboardReceived += text => DispatcherQueue.TryEnqueue(() => opened.RemoteClipboard = text);
            session.StateChanged += s => DispatcherQueue.TryEnqueue(() =>
            {
                if (!_sessions.Contains(opened)) return;
                UpdateBar(); tab.Header = profile.Name + " · " + s;
                if (s == SessionState.Active && !opened.RecordedActive) { opened.RecordedActive = true; _ = Mutate(c => { if (c.Find(profile.Id) is not null) c.MarkConnected(profile.Id, DateTimeOffset.UtcNow); }); view.Focus(FocusState.Programmatic); }
                if (s != SessionState.Active) view.ReleaseHeldInput();
            });
            _sessions.Add(opened); Sessions.TabItems.Add(tab); Sessions.SelectedItem = tab; RefreshSessionState();
            Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == "sessions");
            RefreshSessionPresentation();
            opened.Running = RunSession(opened, profile.ToOptions(), credentials);
            pendingTab = null; pendingView = null; pendingSession = null; // Ownership transferred to the session tab.
        }
        catch (Exception e) { ShowError(e.Message); }
        finally
        {
            if (password is not null) password.Password = "";
            if (token is not null) token.Password = "";
            try
            {
                if (pendingTab is not null) { _sessions.Remove(pendingTab); Sessions.TabItems.Remove(pendingTab.Tab); pendingTab.Stop.Dispose(); RefreshSessionState(); }
                try { if (pendingView is not null) await pendingView.DisposeAsync(); }
                finally { if (pendingSession is not null) await pendingSession.DisposeAsync(); }
            }
            catch (Exception e) { ShowError("Connection setup cleanup failed: " + e.GetType().Name); }
            finally { _dialogOpen = false; }
        }
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
        await CloseSessionAsync(opened);
    }
    private async Task CloseSessionAsync(OpenSession opened)
    {
        if (opened.Closing || !_sessions.Contains(opened)) return;
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
        RefreshSessionPresentation();
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
