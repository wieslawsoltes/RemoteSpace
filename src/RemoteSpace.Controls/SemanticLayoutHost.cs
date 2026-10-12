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
    /// <summary>Attach a cached panel under its live semantic owner. Detach its
    /// children before attaching the panel so stale registrations made during
    /// detached construction are removed by the normal visual-tree lifecycle.
    /// Visual instances and their backing pixels are retained; this is performed
    /// on selection changes only, never per frame or layout pass.</summary>
    public void ShowPanel(Panel? panel)
    {
        if (ReferenceEquals(Child, panel)) return;
        if (panel?.Parent is not null)
            throw new InvalidOperationException("The session panel already belongs to another visual parent.");
        Child = null;
        if (panel is null) return;
        var children = panel.Children.ToArray();
        panel.Children.Clear();
        Child = panel;
        foreach (var child in children) panel.Children.Add(child);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new HostPeer(this);

    private sealed class HostPeer(SemanticLayoutHost owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(SemanticLayoutHost);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override bool IsKeyboardFocusableCore() => false;
    }
}
