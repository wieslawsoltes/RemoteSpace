using System.Runtime.CompilerServices;
using RemoteSpace.Core;
using RemoteSpace.Graphics;

internal static class CursorCompositionRegressions
{
    [ModuleInitializer]
    internal static void Run()
    {
        byte[] desktop = [0x12, 0x34, 0x56, 255, 0xab, 0xcd, 0xef, 255, 0, 0, 0, 0]; // padded row
        var before = (byte[])desktop.Clone();
        var pointer = new RemoteCursor(2, 1, 0, 0, [255, 255, 255, 0, 0xff, 0, 0x0f, 0], [255, 255]);
        var result = new byte[8];
        CursorCompositor.Resolve(pointer, desktop, 2, 1, 12, 0, 0, result);
        if (!result.SequenceEqual(new byte[] { 0xed, 0xcb, 0xa9, 255, 0x54, 0xcd, 0xe0, 255 })) throw new Exception("Monochrome/colored XOR cursor composition failed.");
        if (!desktop.SequenceEqual(before)) throw new Exception("Cursor composition modified the desktop.");
        CursorCompositor.Resolve(pointer with { HotX = 1 }, desktop, 2, 1, 12, 0, 0, result);
        if (result[3] != 0 || result[4] != 0xed || result[5] != 0x34 || result[6] != 0x59) throw new Exception("Negative hotspot clipping failed.");
        CursorCompositor.Resolve(pointer, desktop, 2, 1, 12, int.MaxValue, int.MinValue, result);
        if (result[3] != 0 || result[7] != 0) throw new Exception("Out-of-desktop cursor bounds failed.");
        var alpha = new RemoteCursor(1, 1, 0, 0, [100, 50, 25, 128]);
        var alphaResult = new byte[4];
        CursorCompositor.Resolve(alpha, desktop, 2, 1, 12, 0, 0, alphaResult);
        if (!alphaResult.SequenceEqual(alpha.Rgba)) throw new Exception("Alpha pointer colors must not be premultiplied twice.");
        void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid cursor input accepted."); }
        Reject(() => CursorCompositor.Validate(pointer with { InvertMask = [] }));
        Reject(() => CursorCompositor.Validate(pointer with { HotX = 2 }));
        Reject(() => CursorCompositor.Validate(pointer with { Rgba = [] }));
        Reject(() => CursorCompositor.Resolve(pointer, desktop, 2, 1, 7, 0, 0, result));
        Reject(() => CursorCompositor.Resolve(pointer, desktop.AsSpan(0, 7), 2, 1, 8, 0, 0, result));
        Reject(() => CursorCompositor.Resolve(pointer, desktop, 2, 1, 12, 0, 0, desktop.AsSpan(0, 8)));
        Reject(() => CursorCompositor.Resolve(pointer, desktop, 2, 1, 12, 0, 0, pointer.Rgba));
        Reject(() => CursorCompositor.Resolve(pointer, desktop, 2, 1, 12, 0, 0, new byte[4]));
        Console.WriteLine("PASS 13 cursor composition, immutable desktop, alpha, clipping and bounds regressions");
    }
}
