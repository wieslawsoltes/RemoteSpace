using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RemoteSpace.Core;

namespace RemoteSpace.Controls;

/// <summary>A retained desktop and explicitly collapsible fullscreen connection
/// toolbar. The desktop stays under one semantic geometry owner while the bar's
/// row changes height. The collapsed reveal button is a local overlay, never a
/// child of the remote input surface. The caller owns both supplied controls.</summary>
public sealed class SessionToolbarPresenter : Grid
{
    private readonly ConnectionToolbarState _state = new();
    private readonly SemanticLayoutHost _toolbarHost = new();
    private readonly SemanticLayoutHost _desktopHost = new();
    private readonly SemanticLayoutHost _revealHost = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        Visibility = Visibility.Collapsed
    };
    private readonly Grid _toolbarPanel = new(), _revealPanel = new();
    private readonly FrameworkElement _desktop;
    private readonly Button _collapse;
    private Button? _expand;
    private CancellationTokenSource? _pending;
    public bool IsCollapsed => _state.IsCollapsed;
    public event Action? LocalCommandStarting;
    public event Action<string>? CommandFailed;

    public SessionToolbarPresenter(SessionCommandBar toolbar, FrameworkElement desktop)
    {
        ArgumentNullException.ThrowIfNull(toolbar);
        ArgumentNullException.ThrowIfNull(desktop);
        if (toolbar.Parent is not null || desktop.Parent is not null)
            throw new ArgumentException("The toolbar and desktop must not already have visual parents.");
        _desktop = desktop;
        _toolbarPanel.Children.Add(toolbar);
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        Children.Add(_toolbarHost);
        SetRow(_desktopHost, 1); Children.Add(_desktopHost);
        // Added last, the small native button paints above the full-height
        // desktop. The rest of this row still belongs to the remote view.
        SetRow(_revealHost, 1); Children.Add(_revealHost);
        AutomationProperties.SetName(_toolbarHost, "Session toolbar layout");
        AutomationProperties.SetName(_desktopHost, "Session desktop layout");
        AutomationProperties.SetName(_revealHost, "Collapsed connection toolbar");
        _collapse = toolbar.AddCommand("Collapse connection toolbar", "\uE70E", () => SetCollapsedAsync(true));
        _collapse.Visibility = Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            // Attach only after the retained geometry host is live. Collapse
            // never detaches/recreates the desktop, its queue or framebuffer.
            _desktopHost.Child ??= _desktop;
            ApplyState();
        };
        Unloaded += (_, _) => _pending?.Cancel();
    }

    public void SetFullscreen(bool value)
    {
        VerifyAccess();
        if (!_state.SetFullscreen(value)) return;
        _pending?.Cancel();
        ApplyState();
    }

    /// <summary>Wait for the local activation gesture before hiding its button.
    /// Requests during a transition are coalesced, not queued. Unload or a mode
    /// change cancels pending work, so it cannot collapse a later session.</summary>
    public async Task SetCollapsedAsync(bool value)
    {
        VerifyAccess();
        if (value && !_state.IsFullscreen)
            throw new InvalidOperationException("The connection toolbar can only be collapsed in full screen.");
        if (_pending is not null || value == _state.IsCollapsed) return;
        using var pending = new CancellationTokenSource();
        _pending = pending;
        try
        {
            await CommandActivation.WaitForReleaseAsync(this, pending.Token);
            if (pending.IsCancellationRequested || !IsLoaded) return;
            LocalCommandStarting?.Invoke();
            // A host callback may synchronously end fullscreen or unload this
            // presenter. Recheck admission before committing visual state.
            if (pending.IsCancellationRequested || !IsLoaded) return;
            if (!_state.SetCollapsed(value)) return;
            ApplyState();
            // Local focus remains reachable after either command. It never
            // moves to the remote view as part of a toolbar-only interaction.
            if ((value ? _expand : _collapse) is { } target)
                await ControlFocus.WhenReadyAsync(target, this, pending.Token);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested || !IsLoaded) { }
        finally { if (ReferenceEquals(_pending, pending)) _pending = null; }
    }

    private void VerifyAccess()
    {
        if (!DispatcherQueue.HasThreadAccess)
            throw new InvalidOperationException("Toolbar presentation requires its UI thread.");
    }

    private void ApplyState()
    {
        _collapse.Visibility = _state.IsFullscreen ? Visibility.Visible : Visibility.Collapsed;
        _toolbarHost.Visibility = _state.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        _revealHost.Visibility = _state.IsCollapsed ? Visibility.Visible : Visibility.Collapsed;
        if (!IsLoaded) return;
        // Detaching the toolbar input subtree prevents invisible menu/button
        // peers. Only the bounded bar hierarchy is reattached on an explicit
        // user transition; neither normal layout nor remote frames rebuild it.
        _toolbarHost.ShowPanel(_state.IsCollapsed ? null : _toolbarPanel);
        _revealHost.ShowPanel(_state.IsCollapsed ? _revealPanel : null);
        if (_state.IsCollapsed) EnsureRevealButton();
    }
    private void EnsureRevealButton()
    {
        if (_expand is not null) return;
        _expand = new Button
        {
            Content = new FontIcon { Glyph = "\uE70D", FontSize = 14 },
            Width = 52, Height = 28, Padding = new Thickness(12, 4, 12, 4),
            CornerRadius = new CornerRadius(0, 0, 8, 8)
        };
        // Attach before naming: a detached, named input can otherwise register
        // at the application's fallback root despite having no painted control.
        _revealPanel.Children.Add(_expand);
        AutomationProperties.SetName(_expand, "Expand connection toolbar");
        AutomationProperties.SetHelpText(_expand, "Show the session commands. The remote desktop stays connected.");
        AutomationProperties.SetAutomationId(_expand, "remotespace-session-command-" + Guid.NewGuid().ToString("N"));
        ToolTipService.SetToolTip(_expand, "Expand connection toolbar");
        _expand.Click += async (_, _) =>
        {
            try { await SetCollapsedAsync(false); }
            catch (Exception error) { CommandFailed?.Invoke(error.Message); }
        };
    }
}
