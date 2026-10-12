using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace RemoteSpace.App;

public sealed partial class MainPage
{
    private void InitializeWorkspaceAccessibility()
    {
        // Explicit names keep the pinned Skia semantic tree in sync with the
        // visibly painted heading, result count and empty-state explanation.
        // Property callbacks belong to this page's own controls, not a global
        // observer or a browser-only accessibility overlay.
        NameFromText(Heading);
        NameFromText(DeviceCount);
        NameFromText(EmptyTitle);
        NameFromText(EmptyDescription);
        AutomationProperties.SetHeadingLevel(Heading, AutomationHeadingLevel.Level1);
        AutomationProperties.SetHeadingLevel(EmptyTitle, AutomationHeadingLevel.Level2);
        AutomationProperties.SetLiveSetting(DeviceCount, AutomationLiveSetting.Polite);
    }

    private static void NameFromText(TextBlock text)
    {
        AutomationProperties.SetName(text, text.Text);
        text.RegisterPropertyChangedCallback(TextBlock.TextProperty, static (sender, _) =>
        {
            var source = (TextBlock)sender;
            AutomationProperties.SetName(source, source.Text);
        });
    }
}
