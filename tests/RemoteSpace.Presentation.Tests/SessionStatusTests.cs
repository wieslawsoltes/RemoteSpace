using System.Globalization;
using RemoteSpace.Core;

internal static class SessionStatusTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); checks++; Console.WriteLine("PASS " + name); }
        var cache = new SessionStatusCache();
        var invariant = CultureInfo.InvariantCulture;
        Check(cache.Text == "", "status cache starts empty");
        Check(cache.Update(SessionState.Connecting, 0, 0, true, culture: invariant) && cache.Text == "Connecting · 0 × 0 · Fit", "unpainted connecting status is formatted");
        Check(cache.Update(SessionState.Active, 1280, 720, false, 1.5, invariant) && cache.Text == "Active · 1280 × 720 · 150%", "explicit zoom is independent of remote resolution");
        string text = cache.Text;
        Check(!cache.Update(SessionState.Active, 1280, 720, false, 1.5, invariant) && ReferenceEquals(text, cache.Text), "unchanged status preserves the same string");
        Check(!cache.Update(SessionState.Active, 1280, 720, false, 1.50001, invariant), "rounded equivalent zoom does not publish duplicate visible text");
        Check(cache.Update(SessionState.Active, 1280, 720, true, double.NaN, invariant) && cache.Text.EndsWith("Fit"), "fit ignores irrelevant local zoom");
        Check(!cache.Update(SessionState.Active, 1280, 720, true, 8, invariant), "fit geometry changes do not reformat status");
        Check(cache.Update(SessionState.Active, 1920, 720, true, culture: invariant), "changed desktop width invalidates status");
        Check(cache.Update(SessionState.Active, 1920, 1080, true, culture: invariant), "changed desktop height invalidates status");
        Check(cache.Update(SessionState.Disconnecting, 1920, 1080, true, culture: invariant), "connection state changes invalidate status");
        var mutable = (CultureInfo)invariant.Clone();
        mutable.NumberFormat.PercentSymbol = "percent";
        cache.Update(SessionState.Active, 1920, 1080, false, 1.5, mutable);
        Check(cache.Text.EndsWith("150percent"), "explicit culture is respected");
        mutable.NumberFormat.PercentSymbol = "pct";
        Check(cache.Update(SessionState.Active, 1920, 1080, false, 1.5, mutable) && cache.Text.EndsWith("150pct"), "same mutable culture edits cannot leave stale text");
        var savedCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.ReadOnly(mutable);
            cache.Update(SessionState.Active, 1920, 1080, false, 1.5);
            Check(cache.Text.EndsWith("150pct"), "omitted culture uses current thread culture");
            CultureInfo.CurrentCulture = invariant;
            Check(cache.Update(SessionState.Active, 1920, 1080, false, 1.5) && cache.Text.EndsWith("150%"), "current culture replacement invalidates cached formatting");
        }
        finally { CultureInfo.CurrentCulture = savedCulture; }
        text = cache.Text;
        foreach (double zoom in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 0, double.MaxValue })
        {
            bool rejected = false;
            try { cache.Update(SessionState.Active, 1920, 1080, false, zoom, invariant); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && ReferenceEquals(text, cache.Text), "invalid explicit zoom cannot mutate cached status: " + zoom);
        }
        foreach (var value in new[] { ((SessionState)(-1), 1920, 1080), (SessionState.Active, -1, 1080), (SessionState.Active, 1920, -1) })
        {
            bool rejected = false;
            try { cache.Update(value.Item1, value.Item2, value.Item3, true, culture: invariant); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && ReferenceEquals(text, cache.Text), "invalid status metadata is rejected without partial mutation");
        }
        cache.Update(SessionState.Active, 1920, 1080, true, culture: invariant);
        for (int i = 0; i < 1000; i++) cache.Update(SessionState.Active, 1920, 1080, true, i, invariant);
        long before = GC.GetAllocatedBytesForCurrentThread(); int publications = 0;
        for (int i = 0; i < 100_000; i++) if (cache.Update(SessionState.Active, 1920, 1080, true, i, invariant)) publications++;
        long fitBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(fitBytes == 0 && publications == 0, "100000 unchanged fit updates allocate zero bytes and publish no text");
        cache.Update(SessionState.Active, 1920, 1080, false, 1.5, invariant);
        for (int i = 0; i < 1000; i++) cache.Update(SessionState.Active, 1920, 1080, false, 1.5, invariant);
        before = GC.GetAllocatedBytesForCurrentThread(); publications = 0;
        for (int i = 0; i < 100_000; i++) if (cache.Update(SessionState.Active, 1920, 1080, false, 1.5, invariant)) publications++;
        long zoomBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(zoomBytes == 0 && publications == 0, "100000 unchanged zoom updates allocate zero bytes and publish no text");
        Console.WriteLine($"RESULT: {checks} session status checks passed; fit bytes={fitBytes}, zoom bytes={zoomBytes}.");
    }
}
