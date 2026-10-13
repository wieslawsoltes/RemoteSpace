using System.Buffers;
using RemoteSpace.Core;

namespace RemoteSpace.Input;

public sealed partial class InputState
{
    /// <summary>Admit held-input releases and a complete balanced command as one
    /// queue transaction. A rejected command changes neither held state nor queue.
    /// Single UI owner, like all InputState methods. Existing queued input stays ordered.</summary>
    public bool TryEnqueueCommand(RemoteInputQueue queue, RemoteKeyboardCommand command)
    {
        ArgumentNullException.ThrowIfNull(queue);
        int commandCount = RemoteKeyboardCommands.GetEventCount(command);
        int releases = checked(HeldKeyCount + HeldButtonCount);
        if (releases > queue.Capacity - commandCount) return false;
        int count = releases + commandCount;
        RemoteInput[]? rented = null;
        Span<RemoteInput> events = count <= 64 ? stackalloc RemoteInput[count] :
            (rented = ArrayPool<RemoteInput>.Shared.Rent(count)).AsSpan(0, count);
        try
        {
            CopyReleases(events);
            RemoteKeyboardCommands.Write(command, events[releases..]);
            if (!queue.TryEnqueue(events)) return false;
            ClearHeldState();
            return true;
        }
        finally
        {
            events.Clear();
            if (rented is not null) ArrayPool<RemoteInput>.Shared.Return(rented, clearArray: true);
        }
    }

    private int CopyReleases(Span<RemoteInput> destination)
    {
        int count = HeldKeyCount + HeldButtonCount;
        if (destination.Length < count) throw new ArgumentException("The release destination is too small.", nameof(destination));
        int index = 0;
        foreach (var key in _keys.Values)
            destination[index++] = new(InputKind.ScanCode, (ushort)(0x8000 | (key.Extended ? 0x0100 : 0)), key.Scan);
        foreach (ushort button in _buttons) destination[index++] = new(InputKind.Pointer, button, 0, _x, _y);
        foreach (ushort button in _extendedButtons) destination[index++] = new(InputKind.ExtendedPointer, button, 0, _x, _y);
        return index;
    }

    private void ClearHeldState() { _keys.Clear(); _buttons.Clear(); _extendedButtons.Clear(); }
}
