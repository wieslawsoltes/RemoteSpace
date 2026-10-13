using RemoteSpace.Core;
using RemoteSpace.Input;

namespace RemoteSpace.Controls;

public sealed partial class RemoteDesktopView
{
    /// <summary>Send a user-selected remote chord, releasing tracked physical input
    /// first in the same queue admission. Rejection sends nothing and preserves state.</summary>
    public void SendKeyboardCommand(RemoteKeyboardCommand command)
    {
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("Keyboard commands require the view's UI thread.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session?.State != SessionState.Active || !IsEnabled || !IsLoaded || !IsPresentationActive)
            throw new InvalidOperationException("Select an active, unpaused remote desktop before sending keys.");
        if (_session is IRemoteGraphicsOutput output && !output.OutputEnabled)
            throw new InvalidOperationException("Resume remote updates before sending keys.");
        if (_outbox is null || !_input.TryEnqueueCommand(_outbox, command))
            throw new InvalidOperationException("The input queue cannot accept the complete shortcut. No keys were sent; try again when input has drained.");
    }
}
