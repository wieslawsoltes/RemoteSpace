using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace RemoteSpace.Controls;

/// <summary>Native themed dialog for settings pages whose height changes lazily.
/// The template's moving surface owns its semantic offset, so child controls do
/// not need absolute-position updates when the dialog re-centres. Modal focus,
/// button commands, validation and visuals remain those of ContentDialog.</summary>
public sealed class SettingsContentDialog : ContentDialog
{
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // BackgroundElement is the centred surface in the pinned Uno Fluent
        // template. This is native accessibility metadata, not DOM positioning.
        // Attach the parent boundary before the editor's lazy fields load.
        if (GetTemplateChild("BackgroundElement") is FrameworkElement surface)
            AutomationProperties.SetName(surface, "PC settings dialog surface");
    }
}
