namespace RemoteSpace.Core;

/// <summary>UI-thread state for explicit fullscreen toolbar collapse. Windowed
/// sessions always show their commands. This model owns no timers, native views,
/// remote sessions, or event subscriptions.</summary>
public sealed class ConnectionToolbarState
{
    public bool IsFullscreen { get; private set; }
    public bool IsCollapsed { get; private set; }

    /// <summary>Entering and leaving fullscreen start with expanded commands.
    /// Repeated notifications preserve the user's current collapse choice.</summary>
    public bool SetFullscreen(bool value)
    {
        if (IsFullscreen == value) return false;
        IsFullscreen = value;
        IsCollapsed = false;
        return true;
    }

    /// <summary>Returns whether presentation must change. Collapsing a windowed
    /// toolbar is rejected before state changes; expanding is always safe.</summary>
    public bool SetCollapsed(bool value)
    {
        if (value && !IsFullscreen)
            throw new InvalidOperationException("The connection toolbar can only be collapsed in full screen.");
        if (IsCollapsed == value) return false;
        IsCollapsed = value;
        return true;
    }
}
