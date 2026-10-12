using System.Runtime.CompilerServices;
using RemoteSpace.Graphics;

internal static class ScrollNavigationTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        int checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED " + name); checks++; }
        double Apply(ScrollNavigationCommand command, double value = 50) => ScrollNavigation.Apply(command, value, 0, 100, 3, 40);
        Check(Apply(ScrollNavigationCommand.Start) == 0, "Home");
        Check(Apply(ScrollNavigationCommand.End) == 100, "End");
        Check(Apply(ScrollNavigationCommand.StepBackward) == 47, "backward step");
        Check(Apply(ScrollNavigationCommand.StepForward) == 53, "forward step");
        Check(Apply(ScrollNavigationCommand.PageBackward) == 10, "backward page");
        Check(Apply(ScrollNavigationCommand.PageForward) == 90, "forward page");
        Check(Apply(ScrollNavigationCommand.StepBackward, 1) == 0 && Apply(ScrollNavigationCommand.StepForward, 99) == 100, "step clamps");
        Check(Apply(ScrollNavigationCommand.PageBackward, 1) == 0 && Apply(ScrollNavigationCommand.PageForward, 99) == 100, "page clamps");
        Check(ScrollNavigation.Apply(ScrollNavigationCommand.StepForward, double.MaxValue, 0, double.MaxValue, double.MaxValue, 0) == double.MaxValue, "finite range clamps arithmetic overflow");
        Check(ScrollNavigation.Apply(ScrollNavigationCommand.StepBackward, -double.MaxValue, -double.MaxValue, 0, double.MaxValue, 0) == -double.MaxValue, "finite range clamps arithmetic underflow");
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        foreach (int field in Enumerable.Range(0, 5))
        {
            double[] values = [50, 0, 100, 3, 40]; values[field] = invalid;
            bool rejected = false;
            try { ScrollNavigation.Apply(ScrollNavigationCommand.End, values[0], values[1], values[2], values[3], values[4]); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "nonfinite input rejected");
        }
        foreach (var (min, max, small, large) in new[] { (100d, 0d, 1d, 1d), (0d, 100d, -1d, 1d), (0d, 100d, 1d, -1d) })
        {
            bool rejected = false;
            try { ScrollNavigation.Apply(ScrollNavigationCommand.End, 0, min, max, small, large); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "range policy");
        }
        bool invalidCommand = false;
        try { ScrollNavigation.Apply((ScrollNavigationCommand)99, 50, 0, 100, 1, 1); }
        catch (ArgumentException) { invalidCommand = true; }
        Check(invalidCommand, "invalid command");
        _ = Measure();
        long before = GC.GetAllocatedBytesForCurrentThread();
        double sum = Measure();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(bytes == 0 && sum > 0, "100000 warmed local navigation steps allocate zero bytes");
        Console.WriteLine($"PASS {checks} local-scrollbar navigation checks; 100000 steps allocated {bytes} bytes.");
    }
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static double Measure()
    {
        double sum = 0;
        for (int i = 0; i < 100000; i++) sum += ScrollNavigation.Apply(ScrollNavigationCommand.StepForward, i % 100, 0, 100, 1, 40);
        return sum;
    }
}
