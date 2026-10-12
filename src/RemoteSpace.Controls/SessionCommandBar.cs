using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RemoteSpace.Controls;

/// <summary>Reusable compact connection bar. The host supplies commands and owns
/// session state. Menus are built only when opened; hidden input trees are not
/// retained. Icons have explicit accessible names and tooltips at every width.</summary>
public sealed partial class SessionCommandBar : UserControl
{
    private readonly HashSet<MenuFlyout> _menus = new();
    public event Action<string>? CommandFailed;
    public string Title { get => TitleText.Text; set { TitleText.Text = value; ToolTipService.SetToolTip(TitleText, value); } }
    public string Status { get => StatusText.Text; set { StatusText.Text = value; AutomationProperties.SetName(StatusText, value); } }
    public SessionCommandBar()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, "Remote session commands");
        SizeChanged += (_, e) => Identity.Visibility = e.NewSize.Width < 420 ? Visibility.Collapsed : Visibility.Visible;
        Unloaded += (_, _) => { foreach (var menu in _menus.ToArray()) menu.Hide(); };
    }
    public Button AddCommand(string name, string glyph, Func<Task> execute)
    {
        var button = MakeButton(name, glyph); bool busy = false;
        button.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true;
            try { await execute(); } catch (Exception e) { CommandFailed?.Invoke(e.Message); }
            finally { busy = false; }
        };
        return button;
    }
    public Button AddMenu(string name, string glyph, Func<MenuFlyout> build)
    {
        var button = MakeButton(name, glyph);
        button.Click += (_, _) =>
        {
            try
            {
                foreach (var previous in _menus.ToArray()) previous.Hide();
                var menu = build(); _menus.Add(menu);
                menu.Closed += (_, _) => _menus.Remove(menu);
                menu.ShowAt(button);
            }
            catch (Exception e) { CommandFailed?.Invoke(e.Message); }
        };
        return button;
    }
    public MenuFlyoutItem MenuCommand(string label, Func<Task> execute, bool enabled = true, string? glyph = null)
    {
        var item = new MenuFlyoutItem { Text = label, IsEnabled = enabled };
        if (glyph is not null) item.Icon = new FontIcon { Glyph = glyph };
        item.Click += async (_, _) => { try { await execute(); } catch (Exception e) { CommandFailed?.Invoke(e.Message); } };
        return item;
    }
    private Button MakeButton(string name, string glyph)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 17 }, Width = 36, Height = 36,
            Padding = new Thickness(8), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name);
        Commands.Children.Add(button); return button;
    }
}
