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
            if (await CommandActivation.ShowDialogAsync(this, dialog) != ContentDialogResult.Primary) { password.Password = token.Password = ""; return; }
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
            view.SetScale(profile.GetLocalDisplayScale());
            var toolbar = new SessionCommandBar { Title = profile.Name, Status = "Connecting…" };
            toolbar.CommandFailed += ShowError;
            var panel = new SessionToolbarPresenter(toolbar, view);
            panel.LocalCommandStarting += view.ReleaseHeldInput;
            panel.CommandFailed += ShowError;
            // Keep the live surface in a direct visual sibling container, not TabView's
            // logically reparented content: that host loses the strip offset in
            // its browser semantic bounds, corrupting precise input targeting.
            var tab = new TabViewItem { Header = profile.Name, IsClosable = true };
            AutomationProperties.SetName(tab, profile.Name);
            var opened = new OpenSession(profile, session, view, tab, panel); pendingTab = opened;
            opened.ControlKeys = await SessionControlKeys.AttachAsync(App.MainWindow, this, view, (command, cancellationToken) =>
            {
                if (opened.Closing || !_sessions.Contains(opened) || _section != "sessions" || Sessions.SelectedItem != opened.Tab || session.State != SessionState.Active)
                    throw new InvalidOperationException("Select an active session before using its local shortcuts.");
                // Modifier makes may already have reached RDP. Queue their matching
                // releases before moving focus; the command key itself stays local.
                view.ReleaseHeldInput();
                return command == SessionControlShortcut.ShowConnectionBar
                    ? panel.ShowCommandsAsync(cancellationToken)
                    : ToggleSessionFullscreenAsync(opened);
            }, ShowError);
            toolbar.AddCommand("Fit", "\uE9A6", () => { view.SetScale(null); view.Focus(FocusState.Programmatic); return Task.CompletedTask; });
            AddDisplayControls(session, view, toolbar);
            AddClipboardCommands(toolbar, opened);
            AddSessionActions(toolbar, opened);
            opened.FullScreenButton = toolbar.AddCommand("Full screen", "\uE740", () => ToggleSessionFullscreenAsync(opened));
            ToolTipService.SetToolTip(opened.FullScreenButton, "Full screen (Ctrl+Alt+Pause)");
            AutomationProperties.SetHelpText(view, "Remote keyboard and pointer input. Ctrl+Alt+Home shows local connection commands. Ctrl+Alt+Pause switches full screen. Move focus out to release held input.");
            var status = new SessionStatusCache();
            void UpdateBar()
            {
                if (status.Update(session.State, view.DesktopWidth, view.DesktopHeight, view.FitToWindow, view.Zoom))
                    toolbar.Status = status.Text;
            }
            view.ViewChanged += UpdateBar;
            if (session is IRemoteGraphicsOutput output)
                output.OutputEnabledChanged += enabled => DispatcherQueue.TryEnqueue(() => { if (_sessions.Contains(opened)) view.IsEnabled = enabled; });
            view.InputError += ShowError;
            session.Diagnostic += d => DispatcherQueue.TryEnqueue(() => { while (_diagnostics.Count >= 200) _diagnostics.Dequeue(); _diagnostics.Enqueue($"{d.Time:O} [{d.Stage}] {d.Message}"); });
            session.ClipboardReceived += text => DispatcherQueue.TryEnqueue(() => opened.RemoteClipboard = text);
            session.StateChanged += s => DispatcherQueue.TryEnqueue(() =>
            {
                if (!_sessions.Contains(opened)) return;
                UpdateBar(); opened.Tab.Header = profile.Name + " · " + s;
                if (s == SessionState.Active && !opened.RecordedActive)
                {
                    opened.RecordedActive = true;
                    _ = Mutate(c => { if (c.Find(profile.Id) is not null) c.MarkConnected(profile.Id, DateTimeOffset.UtcNow); });
                    if (_section == "sessions" && Sessions.SelectedItem == opened.Tab) view.Focus(FocusState.Programmatic);
                }
                if (s != SessionState.Active) view.ReleaseHeldInput();
                if (!opened.Closing && s is (SessionState.Disconnected or SessionState.Faulted)) _ = ExitEndedFullscreenAsync(opened);
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
                if (pendingTab is not null) { _sessions.Remove(pendingTab); Sessions.TabItems.Remove(pendingTab.Tab); pendingTab.ControlKeys?.Dispose(); pendingTab.Stop.Dispose(); RefreshSessionState(); RefreshSessionPresentation(); }
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
        // Reserve before awaiting confirmation: repeated commands share the
        // existing close intent rather than opening another confirmation.
        opened.Closing = true;
        bool cleanupStarted = false;
        Exception? failure = null;
        try
        {
            if (opened.Session.State == SessionState.Active && !await Confirm("Disconnect session", "Disconnect from " + opened.Profile.Name + "? Programs running on the remote PC are not closed.", "Disconnect")) return;
            cleanupStarted = true;
            await AsyncCleanupSequence.RunAsync(
                () => { opened.ControlKeys?.Dispose(); return ValueTask.CompletedTask; },
                () => new ValueTask(ExitSessionFullscreenAsync(opened)),
                () => new ValueTask(opened.View.AttachAsync(null)),
                () => { opened.Stop.Cancel(); return ValueTask.CompletedTask; },
                () => opened.Session.DisconnectAsync(),
                () => new ValueTask(opened.Running),
                () => opened.View.DisposeAsync(),
                () => opened.Session.DisposeAsync(),
                () => { opened.Stop.Dispose(); return ValueTask.CompletedTask; });
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (cleanupStarted)
            {
                _sessions.Remove(opened); Sessions.TabItems.Remove(opened.Tab);
                RefreshSessionState(); RefreshSessionPresentation();
            }
            else
            {
                opened.Closing = false;
                // The server may end while confirmation is open. Cancelling
                // that dialog must not strand the now-ended fullscreen view.
                if (opened.Session.State is SessionState.Disconnected or SessionState.Faulted)
                    _ = ExitEndedFullscreenAsync(opened);
            }
        }
        if (failure is not null)
        {
            string message = failure is AggregateException multiple
                ? string.Join("; ", multiple.Flatten().InnerExceptions.Select(error => error.Message))
                : failure.Message;
            ShowError("Session cleanup reported: " + message);
        }
    }
    private void SessionSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        foreach (var opened in _sessions) if (Sessions.SelectedItem != opened.Tab) opened.View.ReleaseHeldInput();
        RefreshSessionPresentation();
    }
    private sealed class OpenSession(ConnectionProfile profile, IRemoteSession session, RemoteDesktopView view, TabViewItem tab, SessionToolbarPresenter panel)
    {
        public ConnectionProfile Profile { get; } = profile;
        public IRemoteSession Session { get; } = session;
        public RemoteDesktopView View { get; } = view;
        public TabViewItem Tab { get; set; } = tab;
        public SessionToolbarPresenter Panel { get; } = panel;
        public Button? FullScreenButton { get; set; }
        public SessionControlKeys? ControlKeys { get; set; }
        public CancellationTokenSource Stop { get; } = new();
        public Task Running { get; set; } = Task.CompletedTask;
        public string? RemoteClipboard { get; set; }
        public bool RecordedActive { get; set; }
        public bool Closing { get; set; }
    }
}
