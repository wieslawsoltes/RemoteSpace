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
    private readonly StackPanel _commands = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    public event Action<string>? CommandFailed;
    public string Title { get => TitleText.Text; set { if (TitleText.Text == value) return; TitleText.Text = value; ToolTipService.SetToolTip(TitleText, value); } }
    public string Status { get => StatusText.Text; set { if (StatusText.Text == value) return; StatusText.Text = value; AutomationProperties.SetName(StatusText, value); } }
    public SessionCommandBar()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, "Remote session commands");
        SizeChanged += (_, e) => Identity.Visibility = e.NewSize.Width < 420 ? Visibility.Collapsed : Visibility.Visible;
        // Realize buttons under their live geometry owner. Detached construction
        // must not leave all six semantic bounds at the bar's origin.
        Loaded += (_, _) => CommandHost.ShowPanel(_commands);
        Unloaded += (_, _) =>
        {
            foreach (var menu in _menus.ToArray()) menu.Hide();
            CommandHost.ShowPanel(null);
        };
    }
    /// <summary>Bring the first visible local command into keyboard focus.</summary>
    public Task FocusCommandsAsync(CancellationToken cancellationToken = default, Button? excludedCommand = null)
    {
        var first = _commands.Children.OfType<Button>().FirstOrDefault(button => !ReferenceEquals(button, excludedCommand) && button.IsEnabled && button.Visibility == Visibility.Visible)
            ?? throw new InvalidOperationException("No session commands are available.");
        return ControlFocus.WhenReadyAsync(first, this, cancellationToken);
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
        var button = MakeButton(name, glyph); bool opening = false;
        button.Click += async (_, _) =>
        {
            if (opening) return;
            opening = true;
            try
            {
                await CommandActivation.WaitForReleaseAsync(this);
                foreach (var previous in _menus.ToArray()) previous.Hide();
                var menu = build(); _menus.Add(menu);
                menu.Closed += (_, _) => _menus.Remove(menu);
                menu.ShowAt(button);
            }
            catch (OperationCanceledException) when (!IsLoaded) { }
            catch (Exception e) { CommandFailed?.Invoke(e.Message); }
            finally { opening = false; }
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
        AutomationProperties.SetAutomationId(button, "remotespace-session-command-" + Guid.NewGuid().ToString("N"));
        AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name);
        _commands.Children.Add(button); return button;
    }
}
