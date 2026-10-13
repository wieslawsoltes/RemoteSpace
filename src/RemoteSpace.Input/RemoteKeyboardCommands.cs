using RemoteSpace.Core;

namespace RemoteSpace.Input;

public enum RemoteKeyboardCommand
{
    SecureAttention, TaskManager, StartMenu, NextApplication, PreviousApplication,
    WindowMenu, PrintScreen, ActiveWindowScreenshot, Escape
}

public readonly record struct RemoteKeyboardCommandInfo(RemoteKeyboardCommand Command, string Label, string Keys);

/// <summary>Explicit remote-only commands. Builds balanced physical scan-code
/// chords, not local OS shortcuts or text. The server's desktop decides the effect.</summary>
public static class RemoteKeyboardCommands
{
    public const int MaximumEvents = 6;
    public static IReadOnlyList<RemoteKeyboardCommandInfo> All { get; } = Array.AsReadOnly(new[]
    {
        new RemoteKeyboardCommandInfo(RemoteKeyboardCommand.SecureAttention, "Windows Security", "Ctrl+Alt+Del"),
        new RemoteKeyboardCommandInfo(RemoteKeyboardCommand.TaskManager, "Task Manager", "Ctrl+Shift+Esc"),
        new RemoteKeyboardCommandInfo(RemoteKeyboardCommand.StartMenu, "Start menu", "Ctrl+Esc"),
        new RemoteKeyboardCommandInfo(RemoteKeyboardCommand.NextApplication, "Next application", "Alt+Tab"),
        new RemoteKeyboardCommandInfo(RemoteKeyboardCommand.PreviousApplication, "Previous application", "Alt+Shift+Tab"),
        new RemoteKeyboardCommandInfo(RemoteKeyboardCommand.WindowMenu, "Window menu", "Alt+Space"),
        new RemoteKeyboardCommandInfo(RemoteKeyboardCommand.PrintScreen, "Print Screen", "Print Screen"),
        new RemoteKeyboardCommandInfo(RemoteKeyboardCommand.ActiveWindowScreenshot, "Active window screenshot", "Alt+Print Screen"),
        new RemoteKeyboardCommandInfo(RemoteKeyboardCommand.Escape, "Escape", "Esc")
    });

    public static int GetEventCount(RemoteKeyboardCommand command) => command switch
    {
        RemoteKeyboardCommand.SecureAttention or RemoteKeyboardCommand.TaskManager or RemoteKeyboardCommand.PreviousApplication => 6,
        RemoteKeyboardCommand.StartMenu or RemoteKeyboardCommand.NextApplication or RemoteKeyboardCommand.WindowMenu or RemoteKeyboardCommand.ActiveWindowScreenshot => 4,
        RemoteKeyboardCommand.PrintScreen or RemoteKeyboardCommand.Escape => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    /// <summary>All modifiers press first and release last, in reverse order.
    /// Returns the written length. Invalid/undersized requests do not touch destination.</summary>
    public static int Write(RemoteKeyboardCommand command, Span<RemoteInput> destination)
    {
        int count = GetEventCount(command);
        if (destination.Length < count) throw new ArgumentException("The keyboard command destination is too small.", nameof(destination));
        // Bit 8 marks an E0 extended scan code, not a character/virtual-key code.
        ReadOnlySpan<ushort> chord = command switch
        {
            RemoteKeyboardCommand.SecureAttention => [0x1d, 0x38, 0x153],
            RemoteKeyboardCommand.TaskManager => [0x1d, 0x2a, 0x01],
            RemoteKeyboardCommand.StartMenu => [0x1d, 0x01],
            RemoteKeyboardCommand.NextApplication => [0x38, 0x0f],
            RemoteKeyboardCommand.PreviousApplication => [0x38, 0x2a, 0x0f],
            RemoteKeyboardCommand.WindowMenu => [0x38, 0x39],
            RemoteKeyboardCommand.PrintScreen => [0x137],
            RemoteKeyboardCommand.ActiveWindowScreenshot => [0x38, 0x137],
            RemoteKeyboardCommand.Escape => [0x01],
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        for (int i = 0; i < chord.Length; i++)
        {
            ushort scan = (ushort)(chord[i] & 0x7f), flags = (ushort)(chord[i] & 0x100);
            destination[i] = new(InputKind.ScanCode, flags, scan);
            destination[count - i - 1] = new(InputKind.ScanCode, (ushort)(flags | 0x8000), scan);
        }
        return count;
    }
}
