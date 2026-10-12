using System.Text.Json;
using RemoteSpace.Core;
using RemoteSpace.Profiles;
using RemoteSpace.Workspace;

internal static class WorkspaceImportRegressions
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool result, string message)
        {
            if (!result) throw new Exception("FAILED " + message);
            checks++;
        }
        void Reject(string json)
        {
            bool rejected = false;
            try { DeviceCatalog.Parse(json); }
            catch (Exception e) when (e is JsonException or FormatException or ArgumentException) { rejected = true; }
            Check(rejected, "Invalid workspace input was accepted");
        }
        Check(DeviceCatalog.Parse("{}").Count == 0, "Empty document preserves initialized schema/settings/device defaults");
        const string id = "20155a26-08cf-4f63-8b46-dc677e5fc36d";
        string minimal = "{\"SchemaVersion\":1,\"Devices\":[{\"Profile\":{\"Id\":\"" + id + "\",\"Name\":\"Imported PC\",\"Host\":\"pc.test.invalid\"}}]}";
        var catalog = DeviceCatalog.Parse(minimal);
        var profile = catalog.Find(Guid.Parse(id))!.Profile;
        Check(profile == new ConnectionProfile { Id = Guid.Parse(id), Name = "Imported PC", Host = "pc.test.invalid" }, "Missing profile fields preserve all connection defaults");
        Check(catalog.Settings == new WorkspaceSettings(), "Omitted settings preserve security-neutral workspace defaults");
        Check(!profile.RedirectClipboard && profile.Security == SecurityMode.NetworkLevelAuthentication && profile.Port == 3389, "Minimal import does not opt into clipboard or weaken authentication");
        var preferences = DeviceCatalog.Parse("{\"Settings\":{\"Theme\":2}}").Settings;
        Check(preferences.Theme == WorkspaceTheme.Dark && preferences.BridgeEndpoint == "", "Partial settings retain omitted endpoint default");
        Check(DeviceCatalog.Parse(catalog.Serialize()).Find(profile.Id)!.Profile == profile, "Default-filled import serializes and reloads identically");
        var complete = profile with { Name = "Żółć 日本語 🙂", Group = "Work", Host = "::1", Port = 3399,
            UserName = "someone", Domain = "DOMAIN", Favorite = true, Width = 1600, Height = 900,
            BitsPerPixel = 16, Security = SecurityMode.Tls, CertificateSha256 = new string('A', 64), RedirectClipboard = true };
        var document = new WorkspaceDocument
        {
            Revision = 17, Settings = new() { Theme = WorkspaceTheme.Dark, Sort = ProfileSort.Group,
                ListView = true, SortDescending = true, BridgeEndpoint = "wss://bridge.test.invalid/session" },
            Devices = [new(complete, DateTimeOffset.Parse("2026-01-02T03:04:05+00:00"))]
        };
        var roundtrip = DeviceCatalog.Parse(new DeviceCatalog(document).Serialize()).Snapshot();
        Check(roundtrip.Settings == document.Settings && roundtrip.Revision == 17 && roundtrip.SchemaVersion == 1 && roundtrip.Devices.SequenceEqual(document.Devices), "All saved fields, recency and preferences survive mapping");
        foreach (string json in new[] { "null", "[]", "{\"Settings\":null}", "{\"Devices\":null}", "{\"Devices\":[null]}",
            "{\"Devices\":[{}]}", "{\"Devices\":[{\"Profile\":null}]}", "{\"Settings\":{\"BridgeEndpoint\":null}}",
            "{\"Settings\":{\"Theme\":99}}", "{\"SchemaVersion\":2}", "{\"Revision\":-1}" }) Reject(json);
        foreach (string member in new[] { "Host", "Name", "Group", "UserName", "Domain" })
            Reject("{\"Devices\":[{\"Profile\":{\"Host\":\"pc.test.invalid\",\"" + member + "\":null}}]}");
        Reject(minimal.Replace("\"Name\":", "\"Port\":0,\"Name\":"));
        Reject(minimal.Replace("\"Name\":", "\"Width\":0,\"Name\":"));
        Reject(minimal.Replace("\"Name\":", "\"Security\":99,\"Name\":"));
        Console.WriteLine($"PASS {checks} generated workspace import/default/null and field-preservation regressions.");
    }
}
