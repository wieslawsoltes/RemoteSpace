using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace RemoteSpace.Controls;

/// <summary>Lazy, accessible inline validation. The text peer is inserted only
/// after its message exists; an initially empty TextBlock can be pruned by Skia
/// and remain absent from the semantic tree when its text subsequently changes.</summary>
public sealed class ValidationMessage : UserControl
{
    public string Message { get; private set; } = "";

    public ValidationMessage()
    {
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(this, "Form validation");
    }

    public void Show(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message == Message) return;
        Message = message;
        Content = null;
        if (message.Length == 0) return;
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(text, message);
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Assertive);
        Content = text;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new MessagePeer(this);
    private sealed class MessagePeer(ValidationMessage owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(ValidationMessage);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override bool IsKeyboardFocusableCore() => false;
    }
}
