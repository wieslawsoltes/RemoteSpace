using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Controls;
using RemoteSpace.Core;

namespace RemoteSpace.App;

public sealed partial class MainPage
{
    private void AddDisplayControls(IRemoteSession session, RemoteDesktopView view, SessionCommandBar bar, SessionDisplayController? controller)
    {
        bar.AddMenu("Display settings", "\uE713", () =>
        {
            var menu = new MenuFlyout();
            menu.Items.Add(bar.MenuCommand("Fit to window", () => { view.SetScale(null); return Task.CompletedTask; }, glyph: view.FitToWindow ? "\uE73E" : null));
            foreach (double scale in new[] { 1d, 1.25, 1.5, 2, 3, 4 })
            {
                double chosen = scale;
                menu.Items.Add(bar.MenuCommand(scale.ToString("0%"), () => { view.SetScale(chosen); return Task.CompletedTask; }, view.DesktopWidth > 0, !view.FitToWindow && view.Zoom == chosen ? "\uE73E" : null));
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(bar.MenuCommand("Zoom in", () => { view.SetScale(Math.Min(4, view.Zoom + .25)); return Task.CompletedTask; }, view.DesktopWidth > 0));
            menu.Items.Add(bar.MenuCommand("Zoom out", () => { view.SetScale(Math.Max(.1, view.Zoom - .25)); return Task.CompletedTask; }, view.DesktopWidth > 0));
            menu.Items.Add(bar.MenuCommand("Top left", () => { view.PanTo(0, 0); return Task.CompletedTask; }, !view.FitToWindow));
            menu.Items.Add(bar.MenuCommand("Bottom right", () => { view.PanTo(double.MaxValue, double.MaxValue); return Task.CompletedTask; }, !view.FitToWindow));
            return menu;
        });
        if (session is not IRemoteDisplayControl display || controller is null) return;
        var button = bar.AddMenu("Remote resolution", "\uE7F4", () =>
        {
            var menu = new MenuFlyout();
            async Task Resize(int width, int height)
            {
                await controller.RequestResolutionAsync(new DisplayMonitor(0, 0, width, height));
            }
            menu.Items.Add(bar.MenuCommand("Update resolution on resize", () =>
            {
                controller.AutomaticResolution = !controller.AutomaticResolution;
                return Task.CompletedTask;
            }, glyph: controller.AutomaticResolution ? "\uE73E" : null));
            menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var (width, height) in new[] { (800,600), (1024,768), (1280,720), (1600,900), (1920,1080), (2560,1440), (3840,2160) })
            {
                bool allowed = false;
                try { if (display.DisplayCapabilities is { } caps) { DisplayLayout.Validate([new DisplayMonitor(0, 0, width, height)], caps); allowed = true; } }
                catch (ArgumentException) { }
                menu.Items.Add(bar.MenuCommand($"{width} × {height}", () => Resize(width, height), allowed, view.DesktopWidth == width && view.DesktopHeight == height ? "\uE73E" : null));
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(bar.MenuCommand("Custom resolution…", async () =>
            {
                if (display.DisplayCapabilities is not { } capabilities)
                    throw new InvalidOperationException("Remote resolution is no longer available.");
                var monitor = await ChooseResolutionAsync(view, capabilities);
                if (monitor is not null) await Resize(monitor.Width, monitor.Height);
            }));
            menu.Items.Add(bar.MenuCommand("Match window", async () =>
            {
                if (!DisplayResizeTarget.TryFromViewport(view.ActualWidth, view.ActualHeight, display.DisplayCapabilities, out var target))
                    throw new InvalidOperationException("This window size is outside the server's display limits.");
                await Resize(target.Width, target.Height);
            }));
            return menu;
        });
        void Availability() => button.IsEnabled = session.State == SessionState.Active && display.DisplayCapabilities is not null;
        display.DisplayCapabilitiesChanged += _ => DispatcherQueue.TryEnqueue(Availability);
        session.StateChanged += _ => DispatcherQueue.TryEnqueue(Availability);
        session.DesktopResized += size => DispatcherQueue.TryEnqueue(() =>
        {
            AutomationProperties.SetHelpText(view, $"Remote desktop {size.Width} × {size.Height}. Display zoom and scrollbars are local; Remote resolution changes the server.");
            Status.Text = $"Remote desktop: {size.Width} × {size.Height} · {view.Renderer}";
        });
        Availability();
    }
}
