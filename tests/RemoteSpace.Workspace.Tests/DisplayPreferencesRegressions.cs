using System.Globalization;
using System.Text.Json;
using RemoteSpace.Core;
using RemoteSpace.Profiles;
using RemoteSpace.Workspace;

internal static class DisplayPreferencesRegressions
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAILED " + name);
            checks++;
        }
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (Exception e) when (e is ArgumentException or FormatException or JsonException) { rejected = true; }
            Check(rejected, name);
        }
        var profile = new ConnectionProfile { Name = "Display PC", Host = "pc.test.invalid", Width = 1600, Height = 900 };
        Check(profile.GetLocalDisplayScale() is null && profile.LocalZoom == 1, "new profiles use initialized fit/100% defaults");
        const string minimal = "{\"Devices\":[{\"Profile\":{\"Host\":\"pc.test.invalid\"}}]}";
        var legacy = DeviceCatalog.Parse(minimal).Snapshot().Devices[0].Profile;
        Check(legacy.FitSessionToWindow && legacy.LocalZoom == 1, "old workspaces retain local presentation defaults");
        Check(legacy.Security == SecurityMode.NetworkLevelAuthentication && !legacy.RedirectClipboard, "display defaults cannot weaken authentication or opt into redirection");
        foreach (bool fit in new[] { false, true })
        foreach (double zoom in new[] { .1, .5, 1, 1.25, 1.375, 1.5, 2, 4 })
        {
            var saved = profile with { FitSessionToWindow = fit, LocalZoom = zoom };
            var catalog = new DeviceCatalog(); catalog.Upsert(saved);
            var restored = DeviceCatalog.Parse(catalog.Serialize()).Find(saved.Id)!.Profile;
            Check(restored == saved, "all local preferences round-trip without rounding");
            Check(restored.GetLocalDisplayScale() == (fit ? (double?)null : zoom), "fit versus fixed local scale is explicit");
            Check(restored.ToOptions() == profile.ToOptions(), "local presentation does not change RDP options, resolution or security");
        }
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, .099, 4.001 })
        {
            var bad = profile with { LocalZoom = invalid };
            Reject(() => bad.GetLocalDisplayScale(), "invalid local zoom rejected even while fitting");
            Reject(() => new DeviceCatalog().Upsert(bad), "invalid local zoom cannot enter a saved catalog");
        }
        foreach (string member in new[] { "\"LocalZoom\":0", "\"LocalZoom\":4.01", "\"LocalZoom\":null", "\"LocalZoom\":\"NaN\"", "\"FitSessionToWindow\":null" })
            Reject(() => DeviceCatalog.Parse(minimal.Replace("\"Host\":", member + ",\"Host\":")), "invalid persisted preference rejected");
        var original = new DeviceCatalog(); original.Upsert(profile with { LocalZoom = 1.5, FitSessionToWindow = false });
        var candidate = original.Fork(); candidate.Upsert(profile with { LocalZoom = 2, FitSessionToWindow = true });
        Check(original.Find(profile.Id)!.Profile.LocalZoom == 1.5 && !original.Find(profile.Id)!.Profile.FitSessionToWindow, "cancelled candidate does not mutate saved preferences");
        Check(original.Duplicate(profile.Id) is { LocalZoom: 1.5, FitSessionToWindow: false }, "duplicate preserves display preferences");
        foreach (bool fit in new[] { false, true })
        {
            var exported = RdpFile.Export(profile with { FitSessionToWindow = fit, LocalZoom = 1.5 });
            var imported = RdpFile.Parse(exported);
            Check(imported.Profile.FitSessionToWindow == fit && imported.Warnings.Count == 0, "standard smart sizing field round-trips explicitly");
            Check(imported.Profile.LocalZoom == 1 && imported.Profile.Width == 1600 && imported.Profile.Height == 900, "local magnification is not mislabeled as RDP server DPI");
        }
        Check(!RdpFile.Parse("full address:s:pc.test.invalid").Profile.FitSessionToWindow, "omitted RDP smart sizing uses the documented disabled default");
        foreach (string setting in new[] { "smart sizing:i:2", "smart sizing:i:-1", "smart sizing:s:1", "smart sizing:i:1\0", "desktopwidth:i:1280\0" })
            Reject(() => RdpFile.Parse("full address:s:pc.test.invalid\n" + setting), "malformed typed RDP preference rejected");
        var unsupported = RdpFile.Parse("full address:s:pc.test.invalid\ndesktopscalefactor:i:150\ndynamic resolution:i:1");
        Check(unsupported.Warnings.Count == 2 && unsupported.Profile.LocalZoom == 1, "unsupported DPI and automatic-resolution flags are not silently applied as local scaling");
        var fixedScale = profile with { FitSessionToWindow = false, LocalZoom = 1.5 };
        for (int i = 0; i < 10; i++) _ = fixedScale.GetLocalDisplayScale();
        long before = GC.GetAllocatedBytesForCurrentThread(); double sum = 0;
        for (int i = 0; i < 100_000; i++) sum += fixedScale.GetLocalDisplayScale()!.Value;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0 && sum == 150_000, "warm local scale selection has no per-read allocation");
        Console.WriteLine($"PASS {checks} saved-display/default/import/isolation regressions; 100000 scale reads allocate {allocated.ToString(CultureInfo.InvariantCulture)} bytes.");
    }
}
