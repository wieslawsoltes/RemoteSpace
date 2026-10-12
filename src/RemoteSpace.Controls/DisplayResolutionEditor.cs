using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Core;

namespace RemoteSpace.Controls;

/// <summary>Reusable custom-resolution editor. The host owns confirmation and
/// sending the request; editing these fields never changes an active session.</summary>
public sealed class DisplayResolutionEditor : UserControl
{
    private readonly DisplayCapabilities _capabilities;
    private readonly TextBox _width = Field("Remote width", "Width (pixels)");
    private readonly TextBox _height = Field("Remote height", "Height (pixels)");

    public DisplayResolutionEditor(int width, int height, DisplayCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _capabilities = capabilities;
        _width.Text = width.ToString(CultureInfo.InvariantCulture);
        _height.Text = height.ToString(CultureInfo.InvariantCulture);
        var fields = new Grid { ColumnSpacing = 12 };
        fields.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        fields.Children.Add(_width); Grid.SetColumn(_height, 1); fields.Children.Add(_height);
        var body = new StackPanel { Spacing = 16, MinWidth = 250, MaxWidth = 440 };
        body.Children.Add(fields);
        body.Children.Add(new TextBlock
        {
            Text = "Changes the remote PC's desktop resolution. Local zoom remains a separate display setting. Dimensions must be 200–8192 pixels and the width must be even.",
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock
        {
            Text = $"Maximum desktop area: {Math.Min(capabilities.MaximumArea, DisplayLayout.MaximumArea):N0} pixels. The server confirms the applied size after display reactivation.",
            TextWrapping = TextWrapping.Wrap, Opacity = .7
        });
        Content = body;
    }

    public DisplayMonitor ReadMonitor() => DisplayResolution.Parse(_width.Text, _height.Text, _capabilities);
    private static TextBox Field(string name, string header)
    {
        var field = new TextBox { Header = header, MaxLength = 16 };
        AutomationProperties.SetName(field, name);
        return field;
    }
}
