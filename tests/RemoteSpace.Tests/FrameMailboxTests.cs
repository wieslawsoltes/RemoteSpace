using RemoteSpace.Core;
using RemoteSpace.Graphics;

internal static class FrameMailboxTests
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        using var box = new FrameMailbox();
        check(!box.TryTake(out _), "empty presentation mailbox has no frame");
        box.Resize(200, 200);
        check(box.TryTake(out var size) && size!.Size == new DesktopSize(200, 200) && size.Update is null, "mailbox announces geometry without inventing an update");
        check(!box.TryTake(out _), "mailbox drains exactly once");
        byte[] red = [255,0,0,255], blue = [0,0,255,255];
        box.Apply(new(1,1,1,1,red)); box.Apply(new(4,3,1,1,blue));
        check(box.TryTake(out var first) && first!.Update is { X:1,Y:1,Width:4,Height:3 }, "mailbox unions pending dirty rectangles");
        var pixels = first!.Update!.Rgba;
        check(pixels.AsSpan(0,4).SequenceEqual(red) && pixels.AsSpan(pixels.Length-4).SequenceEqual(blue) && pixels[7] == 255, "mailbox composites updates and retains opaque unchanged pixels");
        for (int i = 0; i < 10_000; i++) box.Apply(new(1,1,1,1,(i & 1) == 0 ? red : blue));
        check(box.BufferedPixelBytes == 200 * 200 * 4 && box.TryTake(out var latest) && latest!.Update!.Rgba.SequenceEqual(blue), "ten thousand pending updates use one bounded framebuffer and retain the latest pixel");
        check(pixels[0] == 255, "an in-flight snapshot is isolated from later decoder writes");
        box.Apply(new(0,0,1,1,red)); box.Resize(300,200); box.Apply(new(299,199,1,1,blue));
        check(box.TryTake(out var resized) && resized!.Generation > first!.Generation && resized.Size.Width == 300 && resized.Update is { X:299,Y:199,Width:1,Height:1 }, "resize discards old dirty bounds and pairs new pixels with new geometry");
        reject(() => box.Apply(new(300,0,1,1,red)), "mailbox rejects out-of-range server updates");
        check(!box.TryTake(out _), "malformed update cannot dirty the mailbox");
        reject(() => box.Resize(8192,8192), "mailbox allocation ceiling enforced");
        using var concurrent = new FrameMailbox(); concurrent.Resize(200,200);
        Parallel.For(0, 10_000, i => concurrent.Apply(new(i % 200, i / 200, 1, 1, red)));
        check(concurrent.TryTake(out var parallel) && parallel!.Update!.Rgba.Where((_,i) => (i & 3) == 0).All(v => v == 255), "concurrent producers preserve every touched pixel");
        var frame = new FrameBuffer();frame.Resize(2,2);frame.Apply(new(1,1,1,1,blue));
        check(frame.Snapshot(1,1,1,1).Rgba.SequenceEqual(blue), "partial framebuffer snapshot preserves crop coordinates");
        reject(() => frame.Snapshot(int.MaxValue,0,2,1), "snapshot coordinates cannot overflow bounds validation");
        frame.Clear();check(frame.Width == 0 && frame.Snapshot().Rgba.Length == 0, "framebuffer clear releases storage");
        box.Dispose();box.Dispose();
        bool disposed = false;try { box.Apply(new(0,0,1,1,blue)); } catch(ObjectDisposedException) { disposed = true; }
        check(disposed && box.BufferedPixelBytes == 0, "mailbox disposal is idempotent and rejects later writes");
    }
}
