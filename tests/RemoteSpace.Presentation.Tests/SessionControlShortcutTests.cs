using RemoteSpace.Core;

internal static class SessionControlShortcutTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); checks++; Console.WriteLine("PASS " + name); }
        var state = new SessionControlShortcutState();
        foreach (uint key in new uint[] { 0x24, 0x13 })
        {
            for (int flags = 0; flags < 64; flags++)
            {
                bool control = (flags & 1) != 0, alt = (flags & 2) != 0, shift = (flags & 4) != 0,
                    meta = (flags & 8) != 0, altGraph = (flags & 16) != 0, repeat = (flags & 32) != 0;
                bool expected = flags == 3;
                bool result = state.KeyDown(key, control, alt, shift, meta, altGraph, repeat, out var command);
                Check(result == expected && (command != SessionControlShortcut.None) == expected,
                    $"exact local chord admission key={key} flags={flags}");
                if (expected) Check(command == (key == 0x24 ? SessionControlShortcut.ShowConnectionBar : SessionControlShortcut.ToggleFullscreen), "local command identity");
                Check(state.KeyUp(key) == expected && !state.KeyUp(key) && !state.IsHeld, "only admitted key owns its release");
            }
        }
        for (uint key = 0; key < 256; key++)
            if (key is not (0x24 or 0x13) && (state.KeyDown(key, true, true, false, false, false, false, out _) || state.KeyUp(key)))
                throw new Exception("Non-command key was consumed: " + key);
        Check(!state.IsHeld, "ordinary key range retains no input state");
        state.KeyDown(0x24, true, true, false, false, false, false, out _);
        state.KeyDown(0x13, true, true, false, false, false, false, out _);
        Check(state.KeyDown(0x24, false, false, true, true, true, true, out var repeated) && repeated == SessionControlShortcut.None,
            "owned repeat stays local after modifier changes");
        Check(state.KeyUp(0x24) && state.IsHeld && state.KeyUp(0x13) && !state.IsHeld, "simultaneous command keys release independently");
        state.KeyDown(0x24, true, true, false, false, false, false, out _); state.Reset();
        Check(!state.KeyUp(0x24) && !state.IsHeld, "deactivation forgets local ownership");
        for (int i = 0; i < 1000; i++) state.KeyDown(0x41, false, false, false, false, false, false, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int handled = 0;
        for (int i = 0; i < 100_000; i++)
        {
            if (state.KeyDown(0x41, false, false, false, false, false, false, out _)) handled++;
            if (state.KeyDown(0x24, true, true, false, false, false, false, out _)) handled++;
            if (state.KeyUp(0x24)) handled++;
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(bytes == 0 && handled == 200_000 && !state.IsHeld, "100000 ordinary/local gesture cycles allocate zero managed bytes");
        Console.WriteLine($"RESULT: {checks} session control shortcut checks passed; bytes={bytes}.");
    }
}
