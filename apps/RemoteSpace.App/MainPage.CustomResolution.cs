using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Controls;
using RemoteSpace.Core;

namespace RemoteSpace.App;

public sealed partial class MainPage
{
    private async Task<DisplayMonitor?> ChooseResolutionAsync(RemoteDesktopView view, DisplayCapabilities capabilities)
    {
        if (_dialogOpen) return null;
        _dialogOpen = true;
        try
        {
            var editor = new DisplayResolutionEditor(view.DesktopWidth, view.DesktopHeight, capabilities);
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(editor); content.Children.Add(error);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Custom resolution", Content = content,
                PrimaryButtonText = "Apply", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
            };
            DisplayMonitor? monitor = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try { monitor = editor.ReadMonitor(); }
                catch (Exception e) when (e is ArgumentException or FormatException)
                {
                    args.Cancel = true; error.Text = e.Message;
                    AutomationProperties.SetName(error, e.Message);
                }
            };
            return await CommandActivation.ShowDialogAsync(this, dialog) == ContentDialogResult.Primary ? monitor : null;
        }
        finally { _dialogOpen = false; }
    }
}
