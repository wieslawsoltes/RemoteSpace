using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using RemoteSpace.Graphics;
using Windows.System;

namespace RemoteSpace.Controls;

/// <summary>Shared native and semantic keyboard actions for an owned scrollbar.
/// Applying a navigation command never depends on a global focused element.</summary>
internal static class ScrollBarNavigation
{
    internal static bool Apply(ScrollBar bar, VirtualKey key)
    {
        ScrollNavigationCommand command;
        switch (key)
        {
            case VirtualKey.Home: command = ScrollNavigationCommand.Start; break;
            case VirtualKey.End: command = ScrollNavigationCommand.End; break;
            case VirtualKey.PageUp: command = ScrollNavigationCommand.PageBackward; break;
            case VirtualKey.PageDown: command = ScrollNavigationCommand.PageForward; break;
            case VirtualKey.Left when bar.Orientation == Orientation.Horizontal:
            case VirtualKey.Up when bar.Orientation == Orientation.Vertical: command = ScrollNavigationCommand.StepBackward; break;
            case VirtualKey.Right when bar.Orientation == Orientation.Horizontal:
            case VirtualKey.Down when bar.Orientation == Orientation.Vertical: command = ScrollNavigationCommand.StepForward; break;
            default: return false;
        }
        bar.Value = ScrollNavigation.Apply(command, bar.Value, bar.Minimum, bar.Maximum, bar.SmallChange, bar.LargeChange);
        return true;
    }
}
