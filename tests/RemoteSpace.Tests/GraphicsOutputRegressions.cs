using System.Runtime.CompilerServices;
using RemoteSpace.Protocol;

internal static class GraphicsOutputRegressions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var disabled = GraphicsOutputCodec.Suppress(0x103ea, 1007, false, 1280, 720);
        if (Convert.ToHexString(disabled) != "16001700EF03EA030100000108002300000000000000") throw new Exception("Suppress output golden bytes");
        var enabled = GraphicsOutputCodec.Suppress(0x103ea, 1007, true, 1280, 720);
        var data = Activation.ParseData(Activation.ParseShares(enabled).Single().Body);
        if (data.Type != 35 || !data.Body.SequenceEqual(new byte[] { 1,0,0,0,0,0,0,0,255,4,207,2 })) throw new Exception("Inclusive resume rectangle");
        var refresh = Activation.ParseData(Activation.ParseShares(GraphicsOutputCodec.Refresh(0x103ea,1007,1,1)).Single().Body);
        if (refresh.Type != 33 || refresh.Body.Length != 12 || refresh.Body[0] != 1 || refresh.Body.Skip(1).Any(b=>b!=0)) throw new Exception("Single-pixel refresh rectangle");
        foreach (bool supports in new[] { false,true })
        {
            var caps = new PacketWriter().U16(1).U16(0).U16(1).U16(24).Zeros(18).U8(supports?1:0).U8(supports?1:0).ToArray();
            var demand = new PacketWriter().U32(1).U16(0).U16(caps.Length).Bytes(caps).U32(0).ToArray();
            var parsed = GraphicsOutputCodec.ReadCapabilities(demand);
            if (parsed.RefreshRect != supports || parsed.SuppressOutput != supports) throw new Exception("Server output capability flags");
            for (int n=0;n<demand.Length-4;n++)
            {
                try { GraphicsOutputCodec.ReadCapabilities(demand.AsSpan(0,n)); throw new Exception("Truncated capabilities accepted"); }
                catch (RdpProtocolException) { }
            }
        }
        try { GraphicsOutputCodec.Suppress(1,1007,true,8192,8192); throw new Exception("Oversized output accepted"); }
        catch (ArgumentOutOfRangeException) { }
        Console.WriteLine("PASS graphics-output capability, truncation, golden-byte, inclusive-bounds and allocation-limit regressions");
    }
}
