using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Controls;
using RemoteSpace.Core;

namespace RemoteSpace.App;

public sealed partial class MainPage
{
    private WindowFullscreenController _windowFullscreen = null!;
    private OpenSession? _fullScreenSession;
    private bool _isSessionFullscreen;

    private void InitializeFullscreen()
    {
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
        ShellLayout.RowDefinitions[0].Height = new GridLength(active ? 0 : 40);
        TitleBar.Visibility = Status.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        Navigation.IsPaneVisible = !active;
        Heading.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        Sessions.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        bool narrow = WorkspaceContent.ActualWidth < 650;
        WorkspaceContent.Padding = active ? new Thickness(0) : new Thickness(narrow ? 12 : 28, 24, narrow ? 12 : 28, 12);
        WorkspaceFrame.BorderThickness = active ? new Thickness(0) : new Thickness(1, 1, 0, 0);
        WorkspaceFrame.CornerRadius = active ? new CornerRadius(0) : new CornerRadius(8, 0, 0, 0);
        // Re-entry of the pinned native TabView needs fresh selector peers, not
        // new sessions or surfaces. The selected live panel stays attached.
        if (!active && _sessions.Count > 0) RestoreSessionTabs();
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
