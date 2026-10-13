using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RemoteSpace.Controls;
using RemoteSpace.Core;

namespace RemoteSpace.App;

public sealed partial class MainPage
{
    private WindowFullscreenController _windowFullscreen = null!;
    private OpenSession? _fullScreenSession;
    private bool _isSessionFullscreen;
    private readonly SemanticLayoutHost _fullScreenRoot = new() { Visibility = Visibility.Collapsed };
    private readonly SemanticLayoutHost _fullScreenPanel = new();
    private readonly Grid _fullScreenLayout = new();
    private readonly InfoBar _fullScreenNotice = new() { IsClosable = true };

    private void InitializeFullscreen()
    {
        // A separate root avoids NavigationView's retained content-presenter
        // offset when its pane disappears. This is a normal native visual tree,
        // not a correction to browser coordinates or a second remote surface.
        Grid.SetRowSpan(_fullScreenRoot, 3);
        ShellLayout.Children.Add(_fullScreenRoot);
        AutomationProperties.SetName(_fullScreenRoot, "Full-screen session layout");
        AutomationProperties.SetName(_fullScreenPanel, "Full-screen remote session");
        _fullScreenRoot.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
        _fullScreenLayout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _fullScreenLayout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _fullScreenLayout.Children.Add(_fullScreenNotice);
        Grid.SetRow(_fullScreenPanel, 1); _fullScreenLayout.Children.Add(_fullScreenPanel);
        _windowFullscreen = new(this);
        _windowFullscreen.Changed += active =>
        {
            foreach (var session in _sessions) session.View.ReleaseHeldInput();
            if (!active) _fullScreenSession = null;
            ApplyFullscreenLayout(active && _fullScreenSession is not null);
        };
        Unloaded += async (_, _) =>
        {
            try { await _windowFullscreen.DisposeAsync(); }
            catch (Exception e) { System.Diagnostics.Debug.WriteLine("Fullscreen disposal: " + e.GetType().Name); }
        };
    }

    private async Task ToggleSessionFullscreenAsync(OpenSession opened)
    {
        if (opened.Session.State != SessionState.Active || opened.Closing || !_sessions.Contains(opened) || _section != "sessions" || Sessions.SelectedItem != opened.Tab)
            throw new InvalidOperationException("Select a remote session before entering full screen.");
        if (_windowFullscreen.IsFullScreen && ReferenceEquals(_fullScreenSession, opened))
        {
            await _windowFullscreen.SetAsync(false);
            return;
        }
        _fullScreenSession = opened;
        try
        {
            // Do not await focus or module initialization before the permission-sensitive call.
            await _windowFullscreen.SetAsync(true);
            if (opened.Session.State != SessionState.Active || !_sessions.Contains(opened) || opened.Closing || _section != "sessions" || Sessions.SelectedItem != opened.Tab)
                await _windowFullscreen.SetAsync(false);
            else ApplyFullscreenLayout(_windowFullscreen.IsFullScreen);
        }
        catch
        {
            if (!_windowFullscreen.IsFullScreen) { _fullScreenSession = null; ApplyFullscreenLayout(false); }
            throw;
        }
    }

    private async Task ExitSessionFullscreenAsync(OpenSession opened)
    {
        if (!ReferenceEquals(_fullScreenSession, opened)) return;
        await _windowFullscreen.SetAsync(false);
        _fullScreenSession = null;
        ApplyFullscreenLayout(false);
    }
    private async Task ExitEndedFullscreenAsync(OpenSession opened)
    {
        try { await ExitSessionFullscreenAsync(opened); }
        catch (Exception e) { ShowError("Unable to leave full screen: " + e.Message); }
    }

    private void ApplyFullscreenLayout(bool active)
    {
        if (_isSessionFullscreen == active) return;
        _isSessionFullscreen = active;
        if (active)
        {
            SessionContent.ShowPanel(null);
            _fullScreenNotice.IsOpen = false;
            _fullScreenRoot.Visibility = Visibility.Visible;
            _fullScreenRoot.ShowPanel(_fullScreenLayout);
            RefreshSessionPresentation();
            Navigation.Visibility = TitleBar.Visibility = Status.Visibility = Visibility.Collapsed;
        }
        else
        {
            _fullScreenPanel.ShowPanel(null);
            _fullScreenRoot.ShowPanel(null);
            _fullScreenRoot.Visibility = Visibility.Collapsed;
            Navigation.Visibility = TitleBar.Visibility = Status.Visibility = Visibility.Visible;
            bool narrow = WorkspaceContent.ActualWidth < 650;
            WorkspaceContent.Padding = new Thickness(narrow ? 12 : 28, 24, narrow ? 12 : 28, 12);
            // Recreate only selectors whose semantic subtree was hidden. The
            // same live panel, view, framebuffer and connection are retained.
            if (_sessions.Count > 0) RestoreSessionTabs();
            RefreshSessionPresentation();
        }
        foreach (var session in _sessions)
        {
            if (session.FullScreenButton is not { } button) continue;
            bool exit = active && ReferenceEquals(_fullScreenSession, session);
            string name = exit ? "Exit full screen" : "Full screen";
            AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, exit ? "Exit full screen (or use the browser's Escape command)" : name);
            if (button.Content is FontIcon icon) icon.Glyph = exit ? "\uE73F" : "\uE740";
        }
    }
}
