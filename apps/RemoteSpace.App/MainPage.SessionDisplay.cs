using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using RemoteSpace.Controls;
using RemoteSpace.Core;

namespace RemoteSpace.App;

public sealed partial class MainPage
{
    private bool _presentationWired, _tabsVisible, _restoringTabs;
    private void EnsureSessionPresentation()
    {
        if (_presentationWired) return;
        _presentationWired = true;
        Navigation.SelectionChanged += (_, _) => RefreshSessionPresentation();
    }
    private void RefreshSessionPresentation()
    {
        if (SessionContent is null || _restoringTabs) return;
        bool visible = _section == "sessions";
        if (visible && !_tabsVisible) RestoreSessionTabs();
        _tabsVisible = visible;
        var selected = _sessions.FirstOrDefault(s => Sessions.SelectedItem == s.Tab);
        var live = visible ? selected : null;
        if (_isSessionFullscreen)
        {
            SessionContent.ShowPanel(null);
            _fullScreenPanel.ShowPanel(ReferenceEquals(live, _fullScreenSession) ? live?.Panel : null);
        }
        else
        {
            _fullScreenPanel.ShowPanel(null);
            SessionContent.ShowPanel(live?.Panel);
        }
        foreach (var session in _sessions)
            session.View.IsPresentationActive = ReferenceEquals(live, session) &&
                (!_isSessionFullscreen || ReferenceEquals(_fullScreenSession, session));
    }
    private void RestoreSessionTabs()
    {
        // The pinned Skia virtualized-container path restores a shown TabViewItem
        // as a list option and omits its close-button subtree. Realize fresh
        // selector containers on navigation only; keep every live session, view
        // and bitmap. There are at most eight selectors, never a per-frame rebuild.
        var selected = _sessions.FirstOrDefault(s => Sessions.SelectedItem == s.Tab);
        _restoringTabs = true;
        try
        {
            Sessions.TabItems.Clear();
            foreach (var session in _sessions)
            {
                var tab = new TabViewItem { Header = session.Profile.Name + " · " + session.Session.State, IsClosable = true };
                AutomationProperties.SetName(tab, session.Profile.Name);
                session.Tab = tab;
                Sessions.TabItems.Add(tab);
            }
            Sessions.SelectedItem = (selected ?? _sessions.FirstOrDefault())?.Tab;
        }
        finally { _restoringTabs = false; }
    }

    private void AddSessionActions(SessionCommandBar bar, OpenSession opened)
    {
        AddKeyboardCommands(bar, opened);
        bar.AddMenu("Session actions", "\uE712", () =>
        {
            var menu = new MenuFlyout(); bool active = opened.Session.State == SessionState.Active;
            AddGraphicsOutputControls(opened, bar, menu);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(bar.MenuCommand("Ctrl+Alt+Del", () => { opened.View.SendSecureAttention(); return Task.CompletedTask; }, active && opened.View.IsEnabled));
            menu.Items.Add(bar.MenuCommand("Capture", async () =>
            {
                if (await _fileAccess.ExportAsync(opened.Profile.Name + "-desktop.png", opened.View.CapturePng(), "image/png"))
                    Status.Text = OperatingSystem.IsBrowser() ? "Screenshot download requested." : "Screenshot saved.";
            }, opened.View.DesktopWidth > 0));
            menu.Items.Add(bar.MenuCommand("Connection information", () => ShowDialog("Connection information", new TextBlock
            {
                Text = $"{opened.Profile.Name}\nState: {opened.Session.State}\nServer desktop: {opened.View.DesktopWidth} × {opened.View.DesktopHeight}\nLocal display: {(opened.View.FitToWindow ? "Fit to window" : opened.View.Zoom.ToString("0%"))}\nAutomatic resolution: {(opened.Display?.AutomaticResolution == true ? "On" : "Off")}\nAutomatic requests: {opened.Display?.AutomaticRequests ?? 0}\nPan: {opened.View.ViewLayout.HorizontalOffset:0}, {opened.View.ViewLayout.VerticalOffset:0}\nReceived rectangles: {opened.View.AppliedUpdates}\nPresented frames: {opened.View.PresentedFrames}\nScheduled presentations: {opened.View.ScheduledPresentations}\nPixel invalidations: {opened.View.PixelInvalidations}\n\nHidden sessions retain decoded pixels but do not schedule presentation. These counters are not FPS, latency or hardware-GPU measurements.",
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, MaxWidth = 520
            })));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(bar.MenuCommand("Disconnect", () => CloseSessionAsync(opened)));
            return menu;
        });
    }
}
