using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RemoteSpace.Controls;

/// <summary>Read-only information dialog with explicit title/body semantics. The
/// close command has a contextual accessible name while retaining the compact
/// visible caption. Uses the public visual tree, not browser DOM manipulation.</summary>
public static class AccessibleInfoDialog
{
    public static ContentDialog Create(string title, object content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var heading = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetName(heading, title);
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        // Keep the actual framework type so implicit Fluent theme resources,
        // corner radii and button styling are identical to ordinary dialogs.
        var dialog = new ContentDialog { Title = heading, Content = content, CloseButtonText = "Close" };
        if (content is TextBlock text) AutomationProperties.SetName(text, text.Text);
        dialog.Opened += (_, _) => NameCloseButton(dialog, "Close " + title);
        return dialog;
    }

    private static void NameCloseButton(DependencyObject parent, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button { Name: "CloseButton" } close)
                AutomationProperties.SetName(close, name);
            else NameCloseButton(child, name);
        }
    }
}
