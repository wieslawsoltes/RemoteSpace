using RemoteSpace.Profiles;
using RemoteSpace.Workspace;

internal static class WorkspaceReviewRegressions
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool result, string name) { if (!result) throw new Exception("FAILED " + name); checks++; }
        foreach (var (address, fallback, host, port) in new (string, int, string, int)[] {
            (" pc.example ", 3390, "pc.example", 3390), ("pc.example:3391", 3390, "pc.example", 3391),
            ("[::1]", 3390, "::1", 3390), ("[::1]:3391", 3390, "::1", 3391), ("::1", 3390, "::1", 3390),
            ("127.0.0.1:65535", 3389, "127.0.0.1", 65535) })
            Check(RdpFile.ParseAddressWithDefaultPort(address, fallback) == (host, port), "address port resolution");
        foreach (string address in new[] { "pc:0", "pc:65536", "pc:", "[::1", "[::1]:", "" })
        {
            bool rejected = false; try { RdpFile.ParseAddress(address); } catch (FormatException) { rejected = true; }
            Check(rejected, "invalid endpoint rejected");
        }
        var profiles = Enumerable.Range(0, 10_000).Select(i => new ConnectionProfile { Name = $"Node {i:D4}", Host = $"host-{i}", Group = i % 2 == 0 ? "Work" : "Lab", Favorite = i % 3 == 0 }).ToArray();
        var catalog = new DeviceCatalog(); catalog.UpsertRange(profiles);
        catalog.Query("Node 99"); var narrow = catalog.Query("Node 999");
        Check(catalog.LastQueryCandidateCount == 100 && narrow.Count == 10, "prefix refinement scans prior 100 matches, not all 10000");
        Check(catalog.Query("Node 9999").Count == 1 && catalog.LastQueryCandidateCount == 10, "next character scans only prior 10 matches");
        Check(catalog.Query("Node 9").Count == 1000 && catalog.LastQueryCandidateCount == 10000, "backspace restores full search space");
        foreach (bool favorites in new[] { false, true }) foreach (string? group in new string?[] { null, "Work", "Lab" })
        foreach (string search in new[] { "Node", "Node 9", "Node 99", "node 999", "Node 9", "", "HOST-1", "HOST-19", "HOST", "missing", "missing more", "Node 9999" })
        {
            var actual = catalog.Query(search, favorites, group).Select(d => d.Profile.Id);
            var expected = profiles.Where(p => (!favorites || p.Favorite) && (group is null || p.Group == group) &&
                (p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || p.Host.Contains(search, StringComparison.OrdinalIgnoreCase) || p.Group.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id).Select(p => p.Id);
            Check(actual.SequenceEqual(expected), "incremental search equals full reference filter");
        }
        catalog.Query("Node 999"); var fork = catalog.Fork();
        fork.Upsert(profiles[0] with { Name = "Node 99999" });
        Check(fork.Query("Node 9999").Count == 2 && catalog.Query("Node 9999").Count == 1, "edits invalidate prefix cache without mutating parent fork");
        catalog.Configure(catalog.Settings with { SortDescending = true });
        Check(catalog.Query("Node 99")[0].Profile.Name == "Node 9999", "sort change invalidates narrowed order");
        Console.WriteLine($"PASS {checks} endpoint and incremental-search review regressions; prefix scans 100 -> 10 of 10000 devices.");
    }
}
