using System.Runtime.CompilerServices;
using RemoteSpace.Core;
using RemoteSpace.Graphics;

internal static class ContiguousFrameRegressions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var frame = new FrameBuffer(); frame.Resize(8, 6);
        byte[] strip = new byte[8 * 3 * 4]; new Random(9042).NextBytes(strip);
        var original = frame.Snapshot().Rgba;
        strip.CopyTo(original, 2 * 8 * 4);
        frame.Apply(new SurfaceUpdate(0, 2, 8, 3, strip));
        if (!frame.Snapshot().Rgba.SequenceEqual(original)) throw new Exception("Contiguous update changed unrelated rows.");
        var snapshot = frame.Snapshot(0, 2, 8, 3);
        if (!snapshot.Rgba.SequenceEqual(strip) || snapshot.Y != 2) throw new Exception("Contiguous crop mismatch.");
        Array.Clear(strip);
        if (!frame.Snapshot().Rgba.SequenceEqual(original)) throw new Exception("Input storage escaped into framebuffer.");
        Array.Clear(snapshot.Rgba);
        if (!frame.Snapshot().Rgba.SequenceEqual(original)) throw new Exception("Snapshot storage escaped into framebuffer.");
        var crop = frame.Snapshot(1, 3, 3, 2);
        for (int row = 0; row < 2; row++)
            if (!crop.Rgba.AsSpan(row * 12, 12).SequenceEqual(original.AsSpan(((3 + row) * 8 + 1) * 4, 12)))
                throw new Exception("Partial-row snapshot mismatch.");
        Console.WriteLine("PASS contiguous framebuffer update/crop row boundaries and storage isolation");
    }
}
