namespace RemoteSpace.Core;

public enum SessionControlShortcut { None, ShowConnectionBar, ToggleFullscreen }

/// <summary>UI-thread admission for local session control keys, not remote input.
/// Only Home/Pause ownership is retained. Ordinary keys and their contents are
/// never stored. A consumed key stays local until release, even after focus or
/// modifier state changes. Reset on host deactivation and disposal.</summary>
public sealed class SessionControlShortcutState
{
    private byte _held;
    public bool IsHeld => _held != 0;

    /// <summary>Returns whether this key belongs to a local gesture. Command is
    /// None for a consumed repeat. Shift, Windows/Command and AltGraph chords are
    /// deliberately excluded. A repeat first observed after entering the scope
    /// cannot start a new command.</summary>
    public bool KeyDown(uint key, bool control, bool alt, bool shift, bool meta, bool altGraph,
        bool repeat, out SessionControlShortcut command)
    {
        command = SessionControlShortcut.None;
        byte bit = Bit(key);
        if (bit == 0) return false;
        if ((_held & bit) != 0) return true;
        if (repeat || !control || !alt || shift || meta || altGraph) return false;
        _held |= bit;
        command = key == 0x24 ? SessionControlShortcut.ShowConnectionBar : SessionControlShortcut.ToggleFullscreen;
        return true;
    }

    public bool KeyUp(uint key)
    {
        byte bit = Bit(key);
        bool owned = (_held & bit) != 0;
        _held &= (byte)~bit;
        return owned;
    }
    public void Reset() => _held = 0;
    private static byte Bit(uint key) => key switch { 0x24 => 1, 0x13 => 2, _ => 0 };
}
