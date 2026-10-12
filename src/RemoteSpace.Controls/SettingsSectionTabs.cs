using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace RemoteSpace.Controls;

/// <summary>Lazy, retained settings pages with native tab selectors. Inactive
/// input pages are detached, not merely painted underneath the selected page.</summary>
public sealed partial class SettingsSectionTabs : UserControl
{
    private readonly (string Title, Func<Panel> Create)[] _sections;
    private readonly Panel?[] _panels;
    private readonly Grid _body = new() { RowSpacing = 12 };
    private readonly TabView _tabs = new()
    {
        Height = 48, IsAddTabButtonVisible = false, CanDragTabs = false,
        CanReorderTabs = false, TabWidthMode = TabViewWidthMode.SizeToContent
    };
    private readonly SemanticLayoutHost _sectionHost = new();
    private int _selected;
    private bool _realizing;
    public SettingsSectionTabs(params (string Title, Func<Panel> Create)[] sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (sections.Length is < 1 or > 8 || sections.Any(s => string.IsNullOrWhiteSpace(s.Title) || s.Create is null))
            throw new ArgumentException("Supply one to eight named settings pages.", nameof(sections));
        _sections = sections.ToArray(); _panels = new Panel?[sections.Length];
        InitializeComponent();
        // InitializeComponent runs before our parent is attached. Remove that
        // provisional root now; install it beneath the live parent on Loaded,
        // rather than retaining fallback-root semantic coordinates in a dialog.
        Content = null;
        AutomationProperties.SetName(_tabs, "PC settings sections");
        AutomationProperties.SetName(_sectionHost, "Selected PC settings");
        _body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _body.Children.Add(_tabs); Grid.SetRow(_sectionHost, 1); _body.Children.Add(_sectionHost);
        _tabs.SelectionChanged += (_, _) =>
        {
            if (_realizing || !IsLoaded || _tabs.SelectedIndex < 0) return;
            _selected = _tabs.SelectedIndex; ShowSelected();
        };
        Loaded += (_, _) =>
        {
            // Realize the retained hierarchy under one attached geometry owner.
            // Recreate only the small selector set, never the retained editors.
            _realizing = true;
            try
            {
                Content = LayoutOwner;
                LayoutOwner.ShowPanel(_body);
                _tabs.TabItems.Clear();
                foreach (var section in _sections)
                {
                    var tab = new TabViewItem { Header = section.Title, IsClosable = false };
                    AutomationProperties.SetName(tab, section.Title);
                    _tabs.TabItems.Add(tab);
                }
                _tabs.SelectedIndex = _selected;
            }
            finally { _realizing = false; }
            ShowSelected();
        };
        Unloaded += (_, _) =>
        {
            _sectionHost.ShowPanel(null);
            LayoutOwner.ShowPanel(null);
            Content = null;
        };
    }
    private void ShowSelected()
    {
        _panels[_selected] ??= _sections[_selected].Create();
        _sectionHost.ShowPanel(_panels[_selected]);
    }
}
