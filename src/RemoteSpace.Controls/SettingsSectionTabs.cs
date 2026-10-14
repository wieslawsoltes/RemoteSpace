using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RemoteSpace.Core;
using Windows.System;

namespace RemoteSpace.Controls;

/// <summary>Lazy retained settings pages with a directly owned native tab strip.
/// No TabView/ListView presenter is involved: each painted selector and its
/// accessibility peer use the same live geometry parent inside the dialog.</summary>
public sealed partial class SettingsSectionTabs : UserControl
{
    private readonly (string Title, Func<Panel> Create)[] _sections;
    private readonly Panel?[] _panels;
    private readonly Grid _body = new() { RowSpacing = 12 };
    private readonly SectionStrip _strip;
    private readonly StackPanel _selectors = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly SemanticLayoutHost _sectionHost = new();
    private SectionButton[] _buttons = [];
    private int _selected;
    public int SelectedIndex => _selected;

    public SettingsSectionTabs(params (string Title, Func<Panel> Create)[] sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        if (sections.Length is < 1 or > 8 || sections.Any(s => string.IsNullOrWhiteSpace(s.Title) || s.Create is null))
            throw new ArgumentException("Supply one to eight named settings pages.", nameof(sections));
        _sections = sections.ToArray(); _panels = new Panel?[sections.Length];
        _strip = new SectionStrip(this);
        InitializeComponent();
        Content = null;
        _body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _body.Children.Add(_strip); Grid.SetRow(_sectionHost, 1); _body.Children.Add(_sectionHost);
        Loaded += (_, _) =>
        {
            Content = LayoutOwner;
            LayoutOwner.ShowPanel(_body);
            _strip.Child = _selectors;
            AutomationProperties.SetName(_strip, "PC settings sections");
            AutomationProperties.SetName(_sectionHost, "Selected PC settings");
            // Recreate only the bounded selector set when reattaching. Editors
            // remain cached and their values survive section/expander changes.
            _selectors.Children.Clear();
            _buttons = new SectionButton[_sections.Length];
            for (int index = 0; index < _sections.Length; index++)
            {
                var button = new SectionButton(this, index) { Content = _sections[index].Title };
                _buttons[index] = button; _selectors.Children.Add(button);
                AutomationProperties.SetName(button, _sections[index].Title);
                button.UpdateSelection(false);
            }
            ShowSelected();
        };
        Unloaded += (_, _) =>
        {
            _sectionHost.ShowPanel(null);
            _selectors.Children.Clear(); _buttons = [];
            _strip.Child = null;
            LayoutOwner.ShowPanel(null); Content = null;
        };
    }

    private void Select(int index, bool focus)
    {
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("Settings selection requires its UI thread.");
        if (!IsLoaded) return;
        if ((uint)index >= (uint)_sections.Length) throw new ArgumentOutOfRangeException(nameof(index));
        if (_selected != index)
        {
            // Construct before changing selection so a failed page factory
            // leaves the previous selection and editors usable.
            _panels[index] ??= _sections[index].Create() ?? throw new InvalidOperationException("The settings page factory returned null.");
            int previous = _selected; _selected = index;
            ShowSelected();
            _buttons[previous].UpdateSelection(true); _buttons[index].UpdateSelection(true);
        }
        if (focus) _buttons[index].Focus(FocusState.Keyboard);
    }
    private void ShowSelected()
    {
        _panels[_selected] ??= _sections[_selected].Create() ?? throw new InvalidOperationException("The settings page factory returned null.");
        _sectionHost.ShowPanel(_panels[_selected]);
    }

    private sealed class SectionStrip(SettingsSectionTabs owner) : Border
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new StripPeer(this, owner);
    }
    private sealed class StripPeer(SectionStrip strip, SettingsSectionTabs owner) : FrameworkElementAutomationPeer(strip), ISelectionProvider
    {
        protected override string GetClassNameCore() => "SettingsSectionTabs";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tab;
        protected override bool IsKeyboardFocusableCore() => false;
        protected override object GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.Selection ? this : base.GetPatternCore(pattern);
        public bool CanSelectMultiple => false;
        public bool IsSelectionRequired => true;
        public IRawElementProviderSimple[] GetSelection() => owner._buttons.Length == 0 ? [] :
            [ProviderFromPeer(CreatePeerForElement(owner._buttons[owner._selected]))];
    }
    private sealed class SectionButton : Button
    {
        internal readonly SettingsSectionTabs Tabs;
        internal readonly int Index;
        internal bool Selected => Tabs._selected == Index;
        internal SectionButton(SettingsSectionTabs tabs, int index)
        {
            Tabs = tabs; Index = index;
            MinWidth = 88; Height = 40; Padding = new Thickness(16, 6, 16, 6);
            BorderThickness = new Thickness(0, 0, 0, 3);
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            Click += (_, _) => Tabs.Select(Index, false);
            PreviewKeyDown += Navigate;
            ActualThemeChanged += (_, _) => UpdateSelection(false);
        }
        private void Navigate(object sender, KeyRoutedEventArgs args)
        {
            if (args.Handled) return;
            int next = args.Key switch
            {
                VirtualKey.Left => (Index + Tabs._sections.Length - 1) % Tabs._sections.Length,
                VirtualKey.Right => (Index + 1) % Tabs._sections.Length,
                VirtualKey.Home => 0,
                VirtualKey.End => Tabs._sections.Length - 1,
                _ => -1
            };
            if (next < 0) return;
            args.Handled = true; Tabs.Select(next, true);
        }
        internal void UpdateSelection(bool notify)
        {
            IsTabStop = Selected;
            BorderBrush = Selected && Application.Current.Resources.TryGetValue("SystemControlForegroundAccentBrush", out var brush) && brush is Brush accent
                ? accent : new SolidColorBrush(Selected ? Microsoft.UI.Colors.DodgerBlue : Microsoft.UI.Colors.Transparent);
            if (notify && FrameworkElementAutomationPeer.FromElement(this) is SectionPeer peer)
                peer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, !Selected, Selected);
        }
        protected override AutomationPeer OnCreateAutomationPeer() => new SectionPeer(this);
    }
    private sealed class SectionPeer(SectionButton button) : ButtonAutomationPeer(button), ISelectionItemProvider
    {
        protected override string GetClassNameCore() => "SettingsSectionTab";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TabItem;
        protected override object GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.SelectionItem ? this : base.GetPatternCore(pattern);
        public bool IsSelected => button.Selected;
        public IRawElementProviderSimple SelectionContainer => ProviderFromPeer(CreatePeerForElement(button.Tabs._strip));
        public void Select() => button.Tabs.Select(button.Index, false);
        public void AddToSelection() => Select();
        public void RemoveFromSelection()
        {
            if (IsSelected) throw new InvalidOperationException("A settings section must remain selected.");
        }
    }
}
