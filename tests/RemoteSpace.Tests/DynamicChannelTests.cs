using RemoteSpace.Channels;
using RemoteSpace.Core;
using RemoteSpace.Protocol;

internal static class DynamicChannelTests
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        byte[] Create(uint id, int size, string name = "display")
        {
            var w = new PacketWriter().U8(0x10 | size);
            if (size == 0) w.U8((int)id); else if (size == 1) w.U16((int)id); else w.U32(id);
            return w.Bytes(System.Text.Encoding.ASCII.GetBytes(name + "\0")).ToArray();
        }
        DynamicChannels New(uint id = 7, int size = 0)
        {
            var mux = new DynamicChannels(["display"]); mux.Accept([0x50, 0, 1, 0]); mux.Accept(Create(id, size)); return mux;
        }
        foreach (byte version in new byte[] { 1, 2, 3 })
        {
            var caps = version == 1 ? new byte[] { 0x50, 0, version, 0 } : new byte[] { 0x50, 0, version, 0, 1, 0, 2, 0, 3, 0, 4, 0 };
            var mux = new DynamicChannels(["display"]);
            check(mux.Accept(caps).Replies.Single().SequenceEqual(new byte[] { 0x50, 0, 1, 0 }), $"DVC v{version} offer selects implemented v1");
            for (int n = 0; n < caps.Length; n++)
            {
                var truncated = caps[..n]; reject(() => new DynamicChannels([]).Accept(truncated), $"DVC caps v{version} truncation {n}");
            }
        }
        reject(() => new DynamicChannels([]).Accept([0x50, 0, 4, 0]), "DVC unknown version rejected");
        reject(() => new DynamicChannels([]).Accept([0x10, 1, 65, 0]), "DVC creation before negotiation rejected");
        var opened = New(); check(opened.OpenChannelCount == 1, "DVC registered listener opens");
        reject(() => opened.Accept(Create(7, 0)), "DVC duplicate ID rejected");
        check(opened.OpenChannelCount == 0, "DVC protocol failure clears all state");
        var unknown = New().Accept(Create(8, 0, "unavailable"));
        check(Convert.ToHexString(unknown.Replies.Single()) == "1008010000C0" && unknown.Event is null, "DVC unknown listener receives failure HRESULT");
        foreach (var (id, size) in new (uint, int)[] { (0,0), (255,0), (256,1), (65535,1), (65536,2), (uint.MaxValue,2) })
        {
            foreach (int length in new[] { 0, 1, 1590, 1591, 1600, 4096, 65536 })
            {
                var mux = New(id, size); byte[] data = new byte[length]; new Random(41).NextBytes(data);
                DynamicChannelEvent? complete = null;
                byte[][] fragments = DynamicChannels.Fragment(id, data);
                foreach (var part in fragments) { var result = mux.Accept(part); if (result.Event is not null) complete = result.Event; }
                check(complete?.Data.SequenceEqual(data) == true && fragments.All(p => p.Length <= 1600) && mux.BufferedBytes == 0, $"DVC ID {id} length {length} exact fragment roundtrip");
            }
        }
        reject(() => New().Accept([0x33, 7]), "DVC reserved ID width rejected");
        reject(() => New().Accept([0x2c, 7, 0]), "DVC reserved length width rejected");
        reject(() => New().Accept([0x30, 8, 1]), "DVC unknown data channel rejected");
        reject(() => New().Accept([0x60, 7, 1]), "DVC unnegotiated compression rejected");
        reject(() => New().Accept([0x20, 7, 1, 1, 2]), "DVC first fragment overflow rejected");
        var interleaved = New(); interleaved.Accept(Create(8,0));
        interleaved.Accept([0x20, 7, 3, 10]); interleaved.Accept([0x20, 8, 2, 20]);
        check(interleaved.Accept([0x30,7,11,12]).Event!.Data.SequenceEqual(new byte[] {10,11,12}) && interleaved.Accept([0x30,8,21]).Event!.Data.SequenceEqual(new byte[] {20,21}), "interleaved DVC reassembly is isolated");
        var limited = New(); limited.Accept(Create(8,0));
        limited.Accept(new PacketWriter().U8(0x28).U8(7).U32(DynamicChannels.MaximumMessage).U8(1).ToArray());
        reject(() => limited.Accept([0x20,8,2,1]), "DVC total allocation budget enforced across channels");
        check(limited.BufferedBytes == 0, "DVC allocation released after malformed peer");
        var closing = New(); closing.Accept([0x20,7,3,1]);
        check(closing.Accept([0x40,7]).Event!.Kind == DynamicChannelEventKind.Closed && closing.BufferedBytes == 0, "DVC close frees incomplete message");
        check(closing.Accept([0x40,7]).Replies.Length == 0, "DVC duplicate close cannot cause acknowledgement loop");
        var capsBytes = new PacketWriter().U32(5).U32(20).U32(4).U32(4096).U32(2048).ToArray();
        var limits = DisplayControlCodec.ParseCapabilities(capsBytes);
        check(limits.MaximumMonitors == 4 && limits.MaximumArea == DisplayLayout.MaximumArea, "display server area product capped by local allocation policy");
        var one = new DisplayMonitor(0,0,1280,720);
        byte[] layout = DisplayControlCodec.MonitorLayout([one], limits);
        check(Convert.ToHexString(layout) == "0200000038000000280000000100000001000000000000000000000000050000D00200000000000000000000000000006400000064000000", "display single-monitor golden wire bytes");
        for (int n = 0; n < 20; n++) { var shortCaps = capsBytes[..n]; reject(() => DisplayControlCodec.ParseCapabilities(shortCaps), "display caps truncation " + n); }
        var huge = DisplayControlCodec.ParseCapabilities(new PacketWriter().U32(5).U32(20).U32(uint.MaxValue).U32(uint.MaxValue).U32(uint.MaxValue).ToArray());
        check(huge.MaximumArea == DisplayLayout.MaximumArea, "display capability multiplication cannot wrap");
        check(DisplayControlCodec.MonitorLayout([one, new(-1280,0,1280,720,false)], limits).Length == 96, "display complete two-monitor layout supports negative origin");
        reject(() => DisplayControlCodec.MonitorLayout([one with { Width = 1281 }], limits), "odd display width rejected");
        reject(() => DisplayControlCodec.MonitorLayout([one with { Primary = false }], limits), "missing primary display rejected");
        reject(() => DisplayControlCodec.MonitorLayout([one,one], limits), "overlapping/duplicate primary monitors rejected");
        reject(() => DisplayControlCodec.MonitorLayout([one with { Left = int.MinValue }], limits), "display coordinate overflow rejected");
        reject(() => DisplayControlCodec.MonitorLayout([one with { Width = 8192, Height = 8192 }], limits), "display framebuffer budget enforced");
        reject(() => DisplayControlCodec.MonitorLayout([one with { DesktopScaleFactor = 99 }], limits), "invalid desktop scale rejected");
        reject(() => DisplayControlCodec.MonitorLayout([one with { DeviceScaleFactor = 125 }], limits), "invalid device scale rejected");
        reject(() => DisplayControlCodec.MonitorLayout([one], new(0,0)), "display server capability limits enforced");
        check(Mcs.StaticChannelNames(new() { Host = "localhost", RedirectClipboard = false }).SequenceEqual(new[] { "drdynvc" }), "DVC joins without clipboard consent");
        check(Mcs.StaticChannelNames(new() { Host = "localhost", EnableDynamicResolution = false }).Length == 0, "optional DVC channel can be disabled");
    }
}
