using System.Runtime.CompilerServices;
using System.Text.Json;
using RemoteSpace.Core;
using RemoteSpace.Profiles;
using RemoteSpace.Workspace;

internal static class AutomaticDisplayPreferencesTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAILED " + name); checks++; }
        var profile = new ConnectionProfile { Host = "pc.test.invalid", FitSessionToWindow = false, LocalZoom = 1.5 };
        Check(!profile.UpdateResolutionOnResize && profile.GetLocalDisplayScale() == 1.5, "new profiles keep opt-in automatic behavior");
        var legacy = DeviceCatalog.Parse("{\"Devices\":[{\"Profile\":{\"Host\":\"pc.test.invalid\"}}]}").Snapshot().Devices[0].Profile;
        Check(!legacy.UpdateResolutionOnResize, "old workspace files do not silently enable auto resize");
        foreach (bool automatic in new[] { false, true })
        {
            var chosen = profile with { UpdateResolutionOnResize = automatic };
            var catalog = new DeviceCatalog(); catalog.Upsert(chosen);
            var roundtrip = DeviceCatalog.Parse(catalog.Serialize()).Find(chosen.Id)!.Profile;
            Check(roundtrip == chosen, "workspace round trip");
            Check(roundtrip.GetLocalDisplayScale() == (automatic ? (double?)null : 1.5), "auto starts in fit without losing fixed zoom");
            Check(roundtrip.ToOptions() == profile.ToOptions(), "preference is not server DPI or negotiation policy");
            Check(catalog.Duplicate(chosen.Id).UpdateResolutionOnResize == automatic, "duplicate preserves auto preference");
            var candidate = catalog.Fork(); candidate.Upsert(chosen with { UpdateResolutionOnResize = !automatic });
            Check(catalog.Find(chosen.Id)!.Profile.UpdateResolutionOnResize == automatic, "cancelled edit is isolated");
            var rdp = RdpFile.Parse(RdpFile.Export(chosen));
            Check(rdp.Profile.UpdateResolutionOnResize == automatic && rdp.Warnings.Count == 0, "typed dynamic resolution round trip");
        }
        Check(RdpFile.Parse("full address:s:pc.test.invalid").Profile.UpdateResolutionOnResize, "RDP omission follows documented default one");
        foreach (string flag in new[] { "dynamic resolution:i:2", "dynamic resolution:i:-1", "dynamic resolution:s:1", "dynamic resolution:i:1\0" })
        {
            bool rejected = false;
            try { RdpFile.Parse("full address:s:pc.test.invalid\n" + flag); } catch (FormatException) { rejected = true; }
            Check(rejected, "malformed dynamic setting rejected");
        }
        foreach (string value in new[] { "null", "1", "\"true\"" })
        {
            bool rejected = false;
            try { DeviceCatalog.Parse("{\"Devices\":[{\"Profile\":{\"Host\":\"pc.test.invalid\",\"UpdateResolutionOnResize\":" + value + "}}]}"); }
            catch (Exception e) when (e is FormatException or JsonException or ArgumentException) { rejected = true; }
            Check(rejected, "invalid workspace boolean rejected");
        }
        Console.WriteLine($"PASS {checks} automatic-display preference/import/default/isolation checks.");
    }
}
