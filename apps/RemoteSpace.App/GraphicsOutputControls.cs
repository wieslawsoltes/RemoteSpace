using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Core;

namespace RemoteSpace.App;

public sealed partial class MainPage
{
    private void AddGraphicsOutputControls(IRemoteSession session, RemoteSpace.Controls.RemoteDesktopView view, StackPanel toolbar)
    {
        if (session is not IRemoteGraphicsOutput output) return;
        bool busy = false;
        var pause = new Button { Content = "Pause updates", IsEnabled = false };
        var refresh = new Button { Content = "Refresh desktop", IsEnabled = false };
        void Update()
        {
            view.IsEnabled = output.OutputEnabled;
            pause.Content = output.OutputEnabled ? "Pause updates" : "Resume updates";
            pause.IsEnabled = !busy && session.State == SessionState.Active && output.GraphicsOutputCapabilities is { SuppressOutput: true };
            refresh.IsEnabled = !busy && output.OutputEnabled && session.State == SessionState.Active && output.GraphicsOutputCapabilities is { RefreshRect: true };
        }
        async Task Execute(bool toggle)
        {
            if (busy) return;
            busy = true; Update();
            try
            {
                if (toggle)
                {
                    if (output.OutputEnabled) view.ReleaseHeldInput();
                    await output.SetOutputEnabledAsync(!output.OutputEnabled);
                }
                else await output.RequestRefreshAsync();
                Status.Text = toggle ? "Graphics-output change requested. Remote programs continue running." : "Desktop refresh requested.";
            }
            catch (Exception e) { ShowError(e.Message); }
            finally { busy = false; Update(); }
        }
        pause.Click += async (_, _) => await Execute(true);
        refresh.Click += async (_, _) => await Execute(false);
        session.StateChanged += _ => DispatcherQueue.TryEnqueue(Update);
        output.GraphicsOutputCapabilitiesChanged += _ => DispatcherQueue.TryEnqueue(Update);
        output.OutputEnabledChanged += _ => DispatcherQueue.TryEnqueue(Update);
        toolbar.Children.Add(pause); toolbar.Children.Add(refresh); Update();
    }
}
