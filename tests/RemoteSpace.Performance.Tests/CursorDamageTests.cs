using System.Runtime.CompilerServices;
using RemoteSpace.Core;
using RemoteSpace.Graphics;

internal static class CursorDamageTests
{
    [ModuleInitializer]
    internal static void Run()
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAILED: " + name);
            checks++; Console.WriteLine("PASS " + name);
        }
        var damage = new CursorCompositionDamage();
        var cursor = new RemoteCursor(4, 4, 1, 2, new byte[64], new byte[16]);
        cursor.InvertMask![5] = 1; cursor.InvertMask[10] = 255;
        // At (20,20), only [20,22) x [19,21) depends on background pixels.
        Check(!damage.NeedsResolve, "new cursor damage state is clean");
        damage.SetCursor(cursor, 20, 20);
        Check(damage.NeedsResolve, "new XOR shape requires composition");
        damage.MarkResolved(); damage.MoveCursor(20, 20);
        Check(!damage.NeedsResolve, "unchanged pointer and viewport-only paint reuse composed texture");
        foreach (var area in new[] { (19,19,1,2), (22,19,1,2), (20,18,2,1), (20,21,2,1), (0,0,1,1) })
        {
            damage.Invalidate(area.Item1, area.Item2, area.Item3, area.Item4);
            Check(!damage.NeedsResolve, "half-open nonoverlapping cursor damage remains clean " + area);
        }
        foreach (var area in new[] { (20,19,1,1), (21,20,1,1), (0,0,40,40), (19,18,2,2) })
        {
            damage.Invalidate(area.Item1, area.Item2, area.Item3, area.Item4);
            Check(damage.NeedsResolve, "overlapping background changes invalidate cursor " + area);
            damage.MarkResolved();
        }
        damage.MoveCursor(21, 20); Check(damage.NeedsResolve, "cursor movement invalidates background dependence");
        damage.MarkResolved(); damage.InvalidateAll();
        Check(damage.NeedsResolve, "same-sized framebuffer replacement invalidates composition");
        damage.MarkResolved(); damage.SetCursor(cursor, 21, 20);
        Check(damage.NeedsResolve, "explicit repeated shape offer invalidates its pixels");
        damage.MarkResolved();
        try { damage.SetCursor(cursor with { HotX = -1 }, 0, 0); throw new Exception("Invalid shape accepted"); }
        catch (ArgumentException) { Check(!damage.NeedsResolve, "invalid shape leaves preceding cache state intact"); }
        damage.Invalidate(21, 19, 1, 1);
        Check(damage.NeedsResolve, "invalid shape preserves old geometry for subsequent damage");
        damage.MarkResolved();
        foreach (var area in new[] { (-1,0,1,1), (0,-1,1,1), (0,0,0,1), (0,0,1,0) })
        {
            bool rejected = false;
            try { damage.Invalidate(area.Item1, area.Item2, area.Item3, area.Item4); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && !damage.NeedsResolve, "invalid update bounds do not dirty texture " + area);
        }
        foreach (var position in new[] { (int.MinValue,int.MaxValue), (int.MaxValue,int.MinValue), (int.MinValue,int.MinValue), (int.MaxValue,int.MaxValue) })
        {
            damage.MoveCursor(position.Item1, position.Item2); damage.MarkResolved();
            damage.Invalidate(0,0,8192,8192);
            Check(!damage.NeedsResolve, "extreme offscreen cursor arithmetic does not wrap " + position);
        }
        damage.SetCursor(cursor, 0, 0); damage.MarkResolved(); damage.Invalidate(0,0,1,1);
        Check(damage.NeedsResolve, "negative hotspot clipping still invalidates visible dependent pixel");
        foreach (var plain in new[] {
            cursor with { InvertMask = null }, cursor with { InvertMask = new byte[16] },
            new RemoteCursor(-1,-1,0,0,[], Hidden:true), new RemoteCursor(0,0,0,0,[], SystemDefault:true) })
        {
            damage.SetCursor(plain, 2, 3); damage.MoveCursor(4, 5); damage.InvalidateAll(); damage.Invalidate(0,0,100,100);
            Check(!damage.NeedsResolve, "alpha zero-mask hidden and system cursors skip background composition");
        }
        damage.SetCursor(cursor,20,20); damage.Clear(); damage.InvalidateAll();
        Check(!damage.NeedsResolve, "clear releases all damage state");

        // Differential oracle: always resolve through the original compositor,
        // compare with a retained buffer updated only by the new damage planner.
        const int width = 40, height = 32, stride = 176;
        var random = new Random(7926);
        byte[] desktop = new byte[stride*height]; random.NextBytes(desktop);
        var shape = new RemoteCursor(6,5,2,3,new byte[120],new byte[30]);
        random.NextBytes(shape.Rgba);
        for (int i=0;i<30;i++) shape.InvertMask![i] = (byte)(i%3 == 0 ? 1 : 0);
        byte[] cached = new byte[120], expected = new byte[120];
        int x=10,y=12, compositions=0;
        damage.SetCursor(shape,x,y);
        for(int iteration=0;iteration<2000;iteration++)
        {
            switch(iteration%7)
            {
                case 0:
                    x=random.Next(-8,48); y=random.Next(-8,40); damage.MoveCursor(x,y); break;
                case 1: case 2: case 3:
                    int rx=random.Next(width), ry=random.Next(height);
                    int rw=random.Next(1,width-rx+1), rh=random.Next(1,height-ry+1);
                    for(int row=ry;row<ry+rh;row++) random.NextBytes(desktop.AsSpan(row*stride+rx*4,rw*4));
                    damage.Invalidate(rx,ry,rw,rh); break;
                case 4:
                    random.NextBytes(shape.Rgba); damage.SetCursor(shape,x,y); break;
                case 5:
                    random.NextBytes(desktop); damage.InvalidateAll(); break;
                // case 6: a local redraw/zoom/pan changes no source pixels.
            }
            if(damage.NeedsResolve)
            {
                CursorCompositor.Resolve(shape,desktop,width,height,stride,x,y,cached);
                damage.MarkResolved(); compositions++;
            }
            CursorCompositor.Resolve(shape,desktop,width,height,stride,x,y,expected);
            if(!cached.AsSpan().SequenceEqual(expected)) throw new Exception("Cursor differential mismatch at " + iteration);
        }
        Check(compositions <= 2000 - 2000 / 7, "2000 mixed paints avoid unnecessary cursor recompositions");
        Check(true, "2000 randomized cached/original cursor rasters match byte for byte with padded rows and clipping");
        damage.SetCursor(shape,x,y);
        try { CursorCompositor.Resolve(shape,desktop,width,height,1,x,y,cached); throw new Exception("Invalid stride accepted"); }
        catch(ArgumentException) { Check(damage.NeedsResolve, "failed resolution remains dirty until explicitly acknowledged"); }

        damage.SetCursor(cursor,20,20); damage.MarkResolved();
        for(int i=0;i<1000;i++) { damage.Invalidate(100,100,10,10); damage.MoveCursor(20,20); }
        long before = GC.GetAllocatedBytesForCurrentThread(); int required=0;
        for(int i=0;i<100_000;i++)
        {
            damage.Invalidate(100,100,10,10); damage.MoveCursor(20,20);
            if(damage.NeedsResolve) required++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread()-before;
        Check(allocated == 0 && required == 0, "100000 unrelated updates allocate zero bytes and require no cursor resolve");
        damage.Invalidate(20,19,1,1);
        for(int i=0;i<100_000;i++) damage.Invalidate(20,19,1,1);
        Check(damage.NeedsResolve, "overlapping burst retains one dirty state until successful publication");
        damage.MarkResolved(); Check(!damage.NeedsResolve, "one acknowledgement drains the dirty burst");
        Console.WriteLine($"RESULT: {checks} cursor damage checks passed; differential paints=2000, compositions={compositions}, unrelated-update bytes={allocated}.");
    }
}
