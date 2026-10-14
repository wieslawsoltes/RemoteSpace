namespace RemoteSpace.Graphics;

public enum ScrollNavigationCommand { Start, End, StepBackward, StepForward, PageBackward, PageForward }

/// <summary>Allocation-free, bounded scrollbar navigation without host input or
/// focus dependencies. The caller selects a command from its own input device.</summary>
public static class ScrollNavigation
{
    public static double Apply(ScrollNavigationCommand command, double value, double minimum, double maximum,
        double smallChange, double largeChange)
    {
        if (!double.IsFinite(value) || !double.IsFinite(minimum) || !double.IsFinite(maximum) ||
            !double.IsFinite(smallChange) || !double.IsFinite(largeChange) ||
            minimum > maximum || smallChange < 0 || largeChange < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Scroll range, increments and value must be finite and ordered.");
        value = Math.Clamp(value, minimum, maximum);
        double target = command switch
        {
            ScrollNavigationCommand.Start => minimum,
            ScrollNavigationCommand.End => maximum,
            ScrollNavigationCommand.StepBackward => value - smallChange,
            ScrollNavigationCommand.StepForward => value + smallChange,
            ScrollNavigationCommand.PageBackward => value - largeChange,
            ScrollNavigationCommand.PageForward => value + largeChange,
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        // Finite operands may overflow when adding near double.MaxValue. The
        // ordered finite limits still clamp the result to an allowed endpoint.
        return Math.Clamp(target, minimum, maximum);
    }
}
