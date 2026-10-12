using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Controls;
using RemoteSpace.Core;

namespace RemoteSpace.App;

public sealed partial class MainPage
{
    private readonly ITextClipboard _textClipboard = new SystemTextClipboard();
    private void AddClipboardCommands(SessionCommandBar bar, OpenSession opened)
    {
        bool busy = false;
        async Task Execute(bool sending)
        {
            if (busy) return;
            busy = true;
            try
            {
                if (sending)
                {
                    var text = await _textClipboard.ReadTextAsync();
                    if (text is null) throw new InvalidOperationException("The local clipboard does not contain text.");
                    await opened.Session.SendClipboardAsync(text, opened.Stop.Token);
                    Status.Text = $"Clipboard: offered {text.Length} characters to {opened.Profile.Name}.";
                }
                else
                {
                    var text = opened.RemoteClipboard;
                    if (text is null) throw new InvalidOperationException("No remote clipboard text has been received. Copy text on the remote PC first.");
                    await _textClipboard.WriteTextAsync(text);
                    Status.Text = $"Clipboard: copied {text.Length} remote characters to the local clipboard.";
                }
            }
            finally { busy = false; }
        }
        bar.AddMenu("Clipboard", "\uE77F", () =>
        {
            bool allowed = opened.Profile.RedirectClipboard && opened.Session.State == SessionState.Active && !busy;
            var menu = new MenuFlyout();
            menu.Items.Add(bar.MenuCommand("Send clipboard", () => Execute(true), allowed));
            menu.Items.Add(bar.MenuCommand("Copy remote text", () => Execute(false), allowed));
            return menu;
        });
    }
}
