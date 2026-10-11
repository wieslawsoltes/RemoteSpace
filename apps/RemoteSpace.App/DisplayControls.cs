using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Controls;
using RemoteSpace.Core;

namespace RemoteSpace.App;

public sealed partial class MainPage
{
    private void AddDisplayControls(IRemoteSession session, RemoteDesktopView view, StackPanel toolbar)
    {
        AddGraphicsOutputControls(session, view, toolbar);
        if (session is not IRemoteDisplayControl display) return;
        var menu = new MenuFlyout();
        var button = new Button { Content = "Resolution", Flyout = menu, IsEnabled = false };
        AutomationProperties.SetName(button, "Remote resolution");
        ToolTipService.SetToolTip(button, "Change the server desktop resolution without disconnecting. Available after server capability negotiation.");
        async Task Resize(int width, int height)
        {
            try
            {
                view.ReleaseHeldInput();
                await display.RequestDisplayLayoutAsync([new DisplayMonitor(0, 0, width, height)]);
                view.FitToWindow = true;
            }
            catch (Exception e) { ShowError(e.Message); }
        }
        foreach (var (width, height) in new[] { (800,600), (1024,768), (1280,720), (1600,900), (1920,1080), (2560,1440), (3840,2160) })
        {
            var item = new MenuFlyoutItem { Text = $"{width} × {height}" };
            item.Click += async (_, _) => await Resize(width, height); menu.Items.Add(item);
        }
        var match = new MenuFlyoutItem { Text = "Match window" };
        match.Click += async (_, _) =>
        {
            int width = Math.Clamp((int)view.ActualWidth, 200, 8192) & ~1;
            int height = Math.Clamp((int)view.ActualHeight, 200, 8192);
            await Resize(width, height);
        };
        menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(match);
        void Availability() => button.IsEnabled = session.State == SessionState.Active && display.DisplayCapabilities is not null;
        display.DisplayCapabilitiesChanged += _ => DispatcherQueue.TryEnqueue(Availability);
        session.StateChanged += _ => DispatcherQueue.TryEnqueue(Availability);
        session.DesktopResized += size => DispatcherQueue.TryEnqueue(() =>
        {
            // This label reports actual server dimensions, never an optimistic request result.
            AutomationProperties.SetHelpText(view, $"Remote desktop {size.Width} × {size.Height}. Keyboard and pointer input are forwarded to the remote PC.");
            Status.Text = $"Remote desktop: {size.Width} × {size.Height} · {view.Renderer}";
        });
        toolbar.Children.Add(button);
    }
}
