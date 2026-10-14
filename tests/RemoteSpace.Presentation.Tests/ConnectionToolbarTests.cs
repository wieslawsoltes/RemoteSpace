using RemoteSpace.Core;

internal static class ConnectionToolbarTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAILED: " + name);
            checks++; Console.WriteLine("PASS " + name);
        }
        var state = new ConnectionToolbarState();
        Check(!state.IsFullscreen && !state.IsCollapsed, "windowed toolbar starts expanded");
        bool rejected = false;
        try { state.SetCollapsed(true); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && !state.IsCollapsed && !state.IsFullscreen, "windowed collapse is rejected atomically");
        Check(!state.SetCollapsed(false) && !state.SetFullscreen(false), "unchanged windowed state needs no layout");
        Check(state.SetFullscreen(true) && !state.IsCollapsed, "fullscreen initially shows the toolbar");
        Check(state.SetCollapsed(true) && state.IsCollapsed, "fullscreen toolbar can be collapsed");
        Check(!state.SetFullscreen(true) && state.IsCollapsed, "duplicate fullscreen notification preserves collapsed choice");
        Check(!state.SetCollapsed(true), "repeated collapse needs no layout");
        Check(state.SetCollapsed(false) && !state.IsCollapsed, "fullscreen toolbar expands explicitly");
        state.SetCollapsed(true);
        Check(state.SetFullscreen(false) && !state.IsCollapsed, "platform fullscreen exit always restores toolbar");
        Check(state.SetFullscreen(true) && !state.IsCollapsed, "next fullscreen entry starts expanded");
        for (int i = 0; i < 1000; i++) { state.SetCollapsed(true); state.SetCollapsed(false); }
        Check(state.IsFullscreen && !state.IsCollapsed, "repeated collapse cycles preserve fullscreen ownership");
        state.SetCollapsed(true);
        for (int i = 0; i < 1000; i++) { state.SetFullscreen(true); state.SetCollapsed(true); }
        long before = GC.GetAllocatedBytesForCurrentThread(); int changes = 0;
        for (int i = 0; i < 100_000; i++)
        {
            if (state.SetFullscreen(true)) changes++;
            if (state.SetCollapsed(true)) changes++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0 && changes == 0, "100000 unchanged toolbar offers allocate zero bytes and request no layout");
        var other = new ConnectionToolbarState();
        Check(!other.IsCollapsed && state.IsCollapsed, "collapse choices are isolated between presenters");
        Console.WriteLine($"RESULT: {checks} connection toolbar state checks passed; unchanged bytes={allocated}.");
    }
}
