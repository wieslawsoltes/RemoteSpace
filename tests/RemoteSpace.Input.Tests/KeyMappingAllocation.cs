using System.Runtime.CompilerServices;
using RemoteSpace.Input;

internal static class KeyMappingAllocation
{
    [ModuleInitializer]
    internal static void Run()
    {
        InputState.TryMapKey(0x41, out _);
        long before = GC.GetAllocatedBytesForCurrentThread(); int checksum = 0;
        for (int i = 0; i < 100_000; i++)
        {
            if (!InputState.TryMapKey((uint)('A' + i % 26), out var key)) throw new Exception("Letter mapping missing.");
            checksum += key.Scan;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (allocated > 1024 || checksum == 0) throw new Exception("Physical key mapping allocates a table per lookup.");
        Console.WriteLine($"PASS 100000 physical-key lookups allocated {allocated} bytes after warm-up");
    }
}
