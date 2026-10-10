using RemoteSpace.Core;
using RemoteSpace.Input;
using RemoteSpace.Profiles;
using RemoteSpace.Workspace;

static class WorkspaceTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var input = new InputState();
        check(input.KeyDown(0x41) == new RemoteInput(InputKind.ScanCode, 0, 0x1e), "input A physical scan code");
        input.KeyDown(0x41); check(input.HeldKeyCount == 1, "repeat does not duplicate held state");
        check(input.KeyUp(0x41) == new RemoteInput(InputKind.ScanCode, 0x8000, 0x1e), "input A release");
        check(input.KeyUp(0x41) is null, "unmatched release ignored");
        check(input.KeyDown(0xa3)?.Flags == 0x100, "right control extended flag");
        input.Button(0x1000, true, 9, 10); input.Move(20, 30);
        var release = input.ReleaseAll();
        check(release.Length == 2 && release[0].Flags == 0x8100 && release[1].X == 20 && release[1].Y == 30, "focus loss releases keyboard and mouse at latest position");
        check(input.ReleaseAll().Length == 0, "releasing twice is idempotent");
        check(input.KeyDown(0xff) is null, "unknown key not guessed");
        check(input.KeyDown(0x41, 0x10)?.Code == 0x10, "physical layout scan code wins over virtual key");
        check(input.KeyUp(0x41)?.Code == 0x10, "release uses original physical mapping");
        check(input.Wheel(-120, 0, 0)[0].Flags == 0x388, "negative signed wheel encoding");
        check(input.Wheel(600, 0, 0).Select(i => i.Flags & 0x1ff).Sum() == 600, "large wheel delta split without truncation");
        reject(() => input.Wheel(int.MinValue, 0, 0), "wheel overflow rejected");
        reject(() => input.Button(1, true, 0, 0), "unknown mouse button rejected");
        check(InputState.UnicodeText("A\U0001f642").Length == 6, "Unicode surrogate pair input");
        reject(() => InputState.UnicodeText("\ud800"), "unpaired high surrogate rejected");
        reject(() => InputState.UnicodeText("\udc00"), "unpaired low surrogate rejected");
        check(InputState.SecureAttention().Length == 6 && InputState.SecureAttention()[3].Flags == 0x8100, "secure attention press and release sequence");
        var catalog = new DeviceCatalog();
        var a = new ConnectionProfile { Host = "host-a", Name = "Alpha", Group = "Work", Favorite = true };
        var b = new ConnectionProfile { Host = "host-b", Name = "Beta", Group = "Lab" };
        catalog.Upsert(b); catalog.Upsert(a);
        check(catalog.Query()[0].Profile.Id == a.Id, "catalog default name sort");
        check(catalog.Query("HOST-B").Count == 1 && catalog.Query(favoritesOnly:true).Count == 1, "catalog search and favorites");
        check(catalog.Query(group:"Work")[0].Profile.Id == a.Id, "catalog group filter");
        catalog.MarkConnected(b.Id, DateTimeOffset.UtcNow);
        catalog.Configure(new() { Sort = ProfileSort.RecentlyUsed, Theme = WorkspaceTheme.Dark });
        check(catalog.Query()[0].Profile.Id == b.Id, "recently used ordering");
        var copy = DeviceCatalog.Parse(catalog.Serialize());
        check(copy.Query().Count == 2 && copy.Snapshot().Settings.Theme == WorkspaceTheme.Dark, "credential-free workspace roundtrip");
        var snapshot = catalog.Snapshot(); snapshot.Devices[0] = new(a);
        check(catalog.Query().Select(d => d.Profile.Id).Distinct().Count() == 2, "snapshot device array isolation");
        catalog.Upsert(a with { Name = "Renamed" }); check(catalog.Query().Count == 2, "upsert retains device identity");
        check(catalog.Remove(a.Id) && !catalog.Remove(a.Id), "catalog removal result");
        reject(() => DeviceCatalog.Parse("{\"SchemaVersion\":2}"), "future workspace schema rejected");
        reject(() => new DeviceCatalog(new() { Devices = [new(a), new(a)] }), "duplicate persisted IDs rejected");
        reject(() => catalog.Configure(new() { BridgeEndpoint = "wss://host/session?token=secret" }), "bridge token URLs rejected");
        reject(() => catalog.Configure(new() { BridgeEndpoint = "ws://example.com/session" }), "remote cleartext bridge rejected");
        catalog.Configure(new() { BridgeEndpoint = "ws://127.0.0.1:5080/session" });
        check(catalog.Snapshot().Settings.BridgeEndpoint.Contains("127.0.0.1"), "explicit local development bridge accepted");
        reject(() => catalog.Upsert(a with { Name = "bad\nname" }), "control characters in profile name rejected");
    }
}
