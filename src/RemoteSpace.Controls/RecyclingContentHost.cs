using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace RemoteSpace.Controls;

/// <summary>A reusable ItemsRepeater content host which disconnects inactive input
/// subtrees while retaining their visual instances for reuse. Call Prepare from
/// ElementPrepared (after attachment), and Recycle from ElementClearing.
/// The explicit child DataContext prevents the repeater's later context clearing
/// from recreating semantic peers on an already detached child.</summary>
public sealed class RecyclingContentHost : Border
{
    public static readonly DependencyProperty ViewTemplateProperty = DependencyProperty.Register(
        nameof(ViewTemplate), typeof(DataTemplate), typeof(RecyclingContentHost), new PropertyMetadata(null, TemplateChanged));
    public DataTemplate? ViewTemplate { get => (DataTemplate?)GetValue(ViewTemplateProperty); set => SetValue(ViewTemplateProperty, value); }
    private FrameworkElement? _view;
    private object? _item;
    public bool IsPrepared { get; private set; }
    public int VisualCreationCount { get; private set; }

    public RecyclingContentHost() => AutomationProperties.SetName(this, "Saved resource");

    public void Prepare(object? item)
    {
        if (_view is null)
        {
            _view = ViewTemplate?.LoadContent() as FrameworkElement ??
                throw new InvalidOperationException("A recycling host requires a template with a FrameworkElement root.");
            VisualCreationCount++;
        }
        _item = item; IsPrepared = true;
        // Reattach before rebinding: property changes belong to a live named
        // semantic parent, never to the application's fallback accessibility root.
        Child = _view;
        _view.DataContext = item;
    }

    public void Recycle()
    {
        // Clear while this host is still attached. Do not change properties on
        // the detached view: its explicit data context is replaced on Prepare.
        Child = null;
        IsPrepared = false; _item = null;
    }

    private static void TemplateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var host = (RecyclingContentHost)sender;
        host.Child = null; host._view = null;
        if (host.IsPrepared) host.Prepare(host._item);
    }
}
