using System.Collections.Generic;
using System.Diagnostics;
using RemoteSpace.Profiles;
using RemoteSpace.Workspace;

internal static class CatalogRegressions
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAILED " + name); checks++; }
        void Reject(Action action) { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException) { checks++; return; } throw new Exception("Invalid import accepted"); }
        var profiles = Enumerable.Range(0, 10_000).Select(i => new ConnectionProfile { Name = $"PC {9999-i:D5}", Host = $"host-{i}", Group = i % 2 == 0 ? "Engineering" : "Lab", Favorite = i % 7 == 0 }).ToArray();
        var catalog = new DeviceCatalog(); long started = Stopwatch.GetTimestamp(); catalog.UpsertRange(profiles);
        double importMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Check(catalog.Count == 10000 && catalog.Revision == 1, "bulk import is one revision");
        var ordered = catalog.Query();
        Check(ordered[0].Profile.Name == "PC 00000", "cached ordering");
        Check(ReferenceEquals(ordered, catalog.Query()), "last result reused");
        // Warm the runtime and tiered code before measuring cached calls.
        // Keep the allocation bound strict; report the measured bytes on failure.
        for (int i = 0; i < 20_000; i++) _ = catalog.Query();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) _ = catalog.Query();
        long cachedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"MEASUREMENT 10000 warm catalog queries allocated {cachedBytes} bytes.");
        Check(cachedBytes < 1024, "warm query does not allocate device arrays");
        Check(catalog.Find(profiles[29].Id)?.Profile == profiles[29], "indexed lookup");
        catalog.Configure(catalog.Settings with { Theme = WorkspaceTheme.Dark });
        Check(ReferenceEquals(ordered, catalog.Query()), "theme change preserves ordered view");
        var baseline = catalog.Snapshot().Devices;
        foreach (string query in new[] { "PC 000", "HOST-2", "Engineering", "not found", "" })
        {
            var expected = baseline.Where(d => d.Profile.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || d.Profile.Host.Contains(query, StringComparison.OrdinalIgnoreCase) || d.Profile.Group.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Profile.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Profile.Id).ToArray();
            Check(catalog.Query(query).SequenceEqual(expected), "reference search equivalence");
        }
        Check(catalog.Query(group: "Engineering", favoritesOnly: true).All(d => d.Profile.Group == "Engineering" && d.Profile.Favorite), "composed filters");
        catalog.Configure(catalog.Settings with { SortDescending = true });
        Check(catalog.Query()[0].Profile.Name == "PC 09999", "descending order");
        Check(DeviceCatalog.Parse(catalog.Serialize()).Settings.SortDescending, "new setting roundtrip");
        var fork = catalog.Fork(); fork.Remove(profiles[0].Id);
        Check(catalog.Count == 10000 && fork.Count == 9999 && catalog.Find(profiles[0].Id) is not null, "fork isolation");
        var copy = fork.Duplicate(profiles[1].Id);
        Check(copy.Id != profiles[1].Id && !copy.Favorite && fork.Find(copy.Id)?.LastConnected is null, "copy owns new identity and no recency");
        var stamp = DateTimeOffset.UtcNow; catalog.MarkConnected(profiles[3].Id, stamp);
        catalog.UpsertRange([profiles[3] with { Name = "Renamed" }]);
        Check(catalog.Find(profiles[3].Id)?.LastConnected == stamp, "bulk merge preserves recency");
        long revision = catalog.Revision; var original = catalog.Find(profiles[5].Id);
        Reject(() => catalog.UpsertRange([profiles[5] with { Name = "Changed" }, profiles[6] with { Name = "invalid\nname" }]));
        Check(catalog.Revision == revision && catalog.Find(profiles[5].Id) == original, "invalid import leaves all prior state intact");
        Reject(() => catalog.UpsertRange([profiles[4], profiles[4]]));
        Check(catalog.Revision == revision, "duplicate IDs do not partially apply");
        var snapshot = catalog.Snapshot(); snapshot.Devices[0] = new(copy);
        Check(catalog.Find(profiles[0].Id)?.Profile == profiles[0], "public snapshot cannot mutate private storage");
        Reject(() => ((IList<SavedDevice>)catalog.Query())[0] = new(copy));
        Check(catalog.Groups.SequenceEqual(new[] { "Engineering", "Lab" }), "groups sorted and distinct");
        Console.WriteLine($"PASS {checks} catalog transaction/cache/filter regressions; 10000-device import {importMs:F2} ms; 10000 warm queries allocated {cachedBytes} bytes (microbenchmark, not UI latency).");
    }
}
