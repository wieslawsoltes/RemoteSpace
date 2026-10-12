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
    private int _selected;
    private bool _realizing;
    public SettingsSectionTabs(params (string Title, Func<Panel> Create)[] sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (sections.Length is < 1 or > 8 || sections.Any(s => string.IsNullOrWhiteSpace(s.Title) || s.Create is null))
            throw new ArgumentException("Supply one to eight named settings pages.", nameof(sections));
        _sections = sections.ToArray(); _panels = new Panel?[sections.Length];
        InitializeComponent();
        Tabs.SelectionChanged += (_, _) =>
        {
            if (_realizing || !IsLoaded || Tabs.SelectedIndex < 0) return;
            _selected = Tabs.SelectedIndex; ShowSelected();
        };
        Loaded += (_, _) =>
        {
            // The pinned host loses TabViewItem child semantics on re-entry.
            // Recreate only the small selector set, never the retained editors.
            _realizing = true;
            try
            {
                Tabs.TabItems.Clear();
                foreach (var section in _sections)
                {
                    var tab = new TabViewItem { Header = section.Title, IsClosable = false };
                    AutomationProperties.SetName(tab, section.Title);
                    Tabs.TabItems.Add(tab);
                }
                Tabs.SelectedIndex = _selected;
            }
            finally { _realizing = false; }
            ShowSelected();
        };
        Unloaded += (_, _) => SectionHost.ShowPanel(null);
    }
    private void ShowSelected()
    {
        _panels[_selected] ??= _sections[_selected].Create();
        SectionHost.ShowPanel(_panels[_selected]);
    }
}
