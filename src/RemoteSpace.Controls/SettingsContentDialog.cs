using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace RemoteSpace.Controls;

/// <summary>Native themed dialog for settings pages whose height changes lazily.
/// The template's moving surface owns its semantic offset, so child controls do
/// not need absolute-position updates when the dialog re-centres. Modal focus,
/// button commands, validation and visuals remain those of ContentDialog.</summary>
public sealed class SettingsContentDialog : ContentDialog
{
    private int _opening;

    public SettingsContentDialog()
    {
        Opened += (_, _) =>
        {
            int opening = ++_opening;
            // Popup.Opened precedes the final visible-state update. Defer once,
            // then reattach the retained native surface contents under a live
            // parent. Naming a template part cannot reparent semantic children
            // that registered earlier while the template was still detached.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!IsLoaded || opening != _opening ||
                    GetTemplateChild("BackgroundElement") is not Border { Child: { } contents } surface) return;
                var focused = FocusManager.GetFocusedElement(XamlRoot) as Control;
                surface.Child = null;
                surface.Child = contents;
                // Preserve the dialog's original initial focus. Reattachment
                // retains every editor/value and does not rerun button actions.
                if (focused?.IsLoaded == true) focused.Focus(FocusState.Programmatic);
            });
        };
        Closed += (_, _) => ++_opening;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("BackgroundElement") is FrameworkElement surface)
            AutomationProperties.SetName(surface, "PC settings dialog surface");
    }
}
