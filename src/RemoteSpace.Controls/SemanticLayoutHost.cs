using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace RemoteSpace.Controls;

/// <summary>A structural layout boundary with a real, non-focusable automation
/// peer. Use where an ancestor's layout offset can change independently of its
/// children. The Skia semantic tree then retains that offset as a parent instead
/// of flattening children into the root with stale absolute coordinates.</summary>
public sealed class SemanticLayoutHost : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new HostPeer(this);

    private sealed class HostPeer(SemanticLayoutHost owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(SemanticLayoutHost);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override bool IsKeyboardFocusableCore() => false;
    }
}
