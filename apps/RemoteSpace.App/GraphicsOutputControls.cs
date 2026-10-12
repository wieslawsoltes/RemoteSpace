using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Controls;
using RemoteSpace.Core;

namespace RemoteSpace.App;

public sealed partial class MainPage
{
    private void AddGraphicsOutputControls(OpenSession opened, SessionCommandBar bar, MenuFlyout menu)
    {
        if (opened.Session is not IRemoteGraphicsOutput output) return;
        bool active = opened.Session.State == SessionState.Active;
        menu.Items.Add(bar.MenuCommand(output.OutputEnabled ? "Pause updates" : "Resume updates", async () =>
        {
            bool enable = !output.OutputEnabled;
            if (!enable) await opened.View.ReleaseHeldInputAsync(opened.Stop.Token);
            await output.SetOutputEnabledAsync(enable, opened.Stop.Token);
            opened.View.IsEnabled = output.OutputEnabled;
            Status.Text = "Graphics-output change requested. Remote programs continue running.";
        }, active && output.GraphicsOutputCapabilities is { SuppressOutput: true }));
        menu.Items.Add(bar.MenuCommand("Refresh desktop", async () =>
        {
            await output.RequestRefreshAsync(opened.Stop.Token);
            Status.Text = "Desktop refresh requested.";
        }, active && output.OutputEnabled && output.GraphicsOutputCapabilities is { RefreshRect: true }));
    }
}
