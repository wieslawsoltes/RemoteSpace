using System.Runtime.CompilerServices;
using RemoteSpace.Core;

internal static class CustomResolutionRegressions
{
    [ModuleInitializer]
    internal static void Run()
    {
        int checks = 0;
        var caps = new DisplayCapabilities(1, DisplayLayout.MaximumArea);
        void Check(bool condition) { if (!condition) throw new Exception("Custom resolution regression #" + checks); checks++; }
        void Reject(string? width, string? height, DisplayCapabilities? limits = null)
        {
            bool rejected = false;
            try { DisplayResolution.Parse(width, height, limits ?? caps); }
            catch (Exception e) when (e is ArgumentException or FormatException) { rejected = true; }
            Check(rejected);
        }
        foreach (var (w, h) in new[] { (200, 200), (1152, 864), (8192, 2048), (2048, 8192) })
        {
            var monitor = DisplayResolution.Parse(w.ToString(), h.ToString(), caps);
            Check(monitor == new DisplayMonitor(0, 0, w, h));
        }
        Check(DisplayResolution.Parse(" 1152 ", "864", caps).Width == 1152);
        foreach (string? value in new string?[] { null, "", " ", "NaN", "Infinity", "1e3", "0x400", "-200", "+200", "199", "8193", "1152.5", "1,152", "200\0", new string('9', 100_000) })
        {
            Reject(value, "864"); Reject("1152", value);
        }
        Reject("1153", "864");
        Reject("8192", "8192");
        Reject("1152", "864", new(0, DisplayLayout.MaximumArea));
        Reject("1152", "864", new(1, 1152UL * 864 - 1));
        Check(DisplayResolution.Parse("1152", "864", new(1, 1152UL * 864)).Height == 864);
        Console.WriteLine($"PASS {checks} custom-resolution parsing and server-boundary regressions");
    }
}
