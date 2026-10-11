using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Controls;

namespace RemoteSpace.App;

public sealed partial class MainPage
{
    private readonly ITextClipboard _textClipboard = new SystemTextClipboard();
    private void AddClipboardCommands(StackPanel toolbar, OpenSession opened)
    {
        bool busy = false;
        var send = new Button { Content = "Send clipboard", IsEnabled = opened.Profile.RedirectClipboard };
        var copy = new Button { Content = "Copy remote text", IsEnabled = opened.Profile.RedirectClipboard };
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
            catch (Exception e) { ShowError(e.Message); }
            finally { busy = false; }
        }
        send.Click += async (_, _) => await Execute(true);
        copy.Click += async (_, _) => await Execute(false);
        toolbar.Children.Add(send); toolbar.Children.Add(copy);
    }
}
