using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Controls;
using RemoteSpace.Core;

namespace RemoteSpace.App;

public sealed partial class MainPage
{
    private bool _presentationWired;
    private void EnsureSessionPresentation()
    {
        if (_presentationWired) return;
        _presentationWired = true;
        Navigation.SelectionChanged += (_, _) => RefreshSessionPresentation();
    }
    private void RefreshSessionPresentation()
    {
        if (SessionContent is null) return;
        var selected = _sessions.FirstOrDefault(s => Sessions.SelectedItem == s.Tab);
        if (!ReferenceEquals(SessionContent.Child, selected?.Panel)) SessionContent.Child = selected?.Panel;
        foreach (var session in _sessions)
            session.View.IsPresentationActive = _section == "sessions" && ReferenceEquals(selected, session);
    }
    private void AddSessionActions(SessionCommandBar bar, OpenSession opened)
    {
        bar.AddMenu("Session actions", "\uE712", () =>
        {
            var menu = new MenuFlyout(); bool active = opened.Session.State == SessionState.Active;
            AddGraphicsOutputControls(opened, bar, menu);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(bar.MenuCommand("Ctrl+Alt+Del", () => { opened.View.SendSecureAttention(); return Task.CompletedTask; }, active));
            menu.Items.Add(bar.MenuCommand("Capture", async () =>
            {
                if (await _fileAccess.ExportAsync(opened.Profile.Name + "-desktop.png", opened.View.CapturePng(), "image/png"))
                    Status.Text = OperatingSystem.IsBrowser() ? "Screenshot download requested." : "Screenshot saved.";
            }, opened.View.DesktopWidth > 0));
            menu.Items.Add(bar.MenuCommand("Connection information", () => ShowDialog("Connection information", new TextBlock
            {
                Text = $"{opened.Profile.Name}\nState: {opened.Session.State}\nServer desktop: {opened.View.DesktopWidth} × {opened.View.DesktopHeight}\nLocal display: {(opened.View.FitToWindow ? "Fit to window" : opened.View.Zoom.ToString("0%"))}\nPan: {opened.View.ViewLayout.HorizontalOffset:0}, {opened.View.ViewLayout.VerticalOffset:0}\nReceived rectangles: {opened.View.AppliedUpdates}\nPresented frames: {opened.View.PresentedFrames}\nScheduled presentations: {opened.View.ScheduledPresentations}\nPixel invalidations: {opened.View.PixelInvalidations}\n\nHidden sessions retain decoded pixels but do not schedule presentation. These counters are not FPS, latency or hardware-GPU measurements.",
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, MaxWidth = 520
            })));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(bar.MenuCommand("Disconnect", () => CloseSessionAsync(opened)));
            return menu;
        });
    }
}
