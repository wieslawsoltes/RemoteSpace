using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace RemoteSpace.Controls;

/// <summary>A themed settings expander whose container groups its editors instead
/// of presenting the entire expanded form as an actionable button. The native
/// template's header toggle remains the keyboard/click target. The standard
/// expand/collapse automation provider and notifications are retained.</summary>
public sealed class SettingsExpander : Expander
{
    public SettingsExpander() => DefaultStyleKey = typeof(Expander);

    protected override AutomationPeer OnCreateAutomationPeer() => new SettingsPeer(this);

    private sealed class SettingsPeer(SettingsExpander owner) : ExpanderAutomationPeer(owner)
    {
        // The Skia host emits a real HTML button for Button control types. A
        // button must not contain the form's other buttons, inputs or comboboxes:
        // action targeting can otherwise resolve an editor to this ancestor.
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override bool IsKeyboardFocusableCore() => false;
        protected override bool HasKeyboardFocusCore() => false;
    }
}
