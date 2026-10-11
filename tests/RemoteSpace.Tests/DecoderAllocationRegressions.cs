using System.Runtime.CompilerServices;
using RemoteSpace.Graphics;
using RemoteSpace.Protocol;

internal static class DecoderAllocationRegressions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var random = new Random(5917);
        uint[] palette = Enumerable.Range(0, 256).Select(i => (uint)((i << 16) | ((255-i) << 8) | i)).ToArray();
        int checks = 0;
        foreach (int bpp in new[] { 8, 15, 16, 24, 32 })
        foreach (int height in new[] { 1, 2, 13 })
        foreach (int width in new[] { 1, 17, 64 })
        {
            int size = (bpp+7)/8, stride = (width*size+3)&~3;
            byte[] raw = new byte[stride*height], expected = new byte[width*height*4];
            var rle = new PacketWriter().U8(0xf4).U16(width*height);
            for (int y=0; y<height; y++) for (int x=0; x<width; x++)
            {
                uint p = (uint)random.Next(256);
                if (bpp != 8) p |= (uint)random.Next(256)<<8;
                if (bpp is 24 or 32) p |= (uint)random.Next(256)<<16;
                int at=y*stride+x*size;
                for (int b=0; b<size; b++) raw[at+b]=(byte)(p>>(8*b));
                rle.Bytes(raw.AsSpan(at,size));
                int dst=((height-1-y)*width+x)*4;
                uint color=bpp==8 ? palette[p] : p;
                uint r=bpp==15 ? (p>>10)&31 : bpp==16 ? (p>>11)&31 : (color>>16)&255;
                uint g=bpp==15 ? (p>>5)&31 : bpp==16 ? (p>>5)&63 : (color>>8)&255;
                uint blue=bpp is 15 or 16 ? p&31 : color&255;
                expected[dst]=(byte)(bpp is 15 or 16 ? (r<<3)|(r>>2) : r);
                expected[dst+1]=(byte)(bpp==15 ? (g<<3)|(g>>2) : bpp==16 ? (g<<2)|(g>>4) : g);
                expected[dst+2]=(byte)(bpp is 15 or 16 ? (blue<<3)|(blue>>2) : blue);
                expected[dst+3]=255;
            }
            if (!BitmapDecoder.Decode(raw,width,height,bpp,false,palette).SequenceEqual(expected)) throw new Exception("Uncompressed color/row regression");
            checks++;
            if (bpp!=32)
            {
                if (!BitmapDecoder.Decode(rle.ToArray(),width,height,bpp,true,palette).SequenceEqual(expected)) throw new Exception("In-place RLE color/row regression");
                checks++;
            }
        }
        byte[] solid=new PacketWriter().U8(0xf3).U16(20000).Bytes([3,2,1]).ToArray();
        _=BitmapDecoder.Decode(solid,200,100,24,true);
        long before=GC.GetAllocatedBytesForCurrentThread();
        byte[] decoded=BitmapDecoder.Decode(solid,200,100,24,true);
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        if (decoded.Length!=80000 || allocated>decoded.Length+4096) throw new Exception("RLE decoder allocated an extra full-size pixel buffer");
        var channel=new ChannelAssembler();
        channel.Accept(new PacketWriter().U32(100).U32(1).U8(42).ToArray());
        try { channel.Accept(new PacketWriter().U32(100).U32(2).ToArray()); throw new Exception("Incomplete channel accepted"); }
        catch (RdpProtocolException) { }
        if (channel.BufferedBytes!=0) throw new Exception("Malformed static channel retained its allocation");
        if (!channel.Accept(ChannelAssembler.Fragment([9,8,7]).Single())!.SequenceEqual(new byte[] {9,8,7})) throw new Exception("Static channel did not reset");
        var fast=new FastPathAssembler(); fast.Accept([0x21,1,0,1], (_,_)=>{});
        try { fast.Accept([0x12,0,0],(_,_)=>{}); throw new Exception("Mismatched fast path accepted"); } catch(RdpProtocolException) { }
        bool completed=false; fast.Accept([1,1,0,7], (code,data)=>completed=code==1 && data.SequenceEqual(new byte[] {7}));
        if (!completed) throw new Exception("Fast path did not reset after malformed input");
        Console.WriteLine($"PASS {checks} bitmap format/orientation checks; RLE allocation {allocated} bytes for {decoded.Length} output bytes; static/fast-path reset regressions");
    }
}
