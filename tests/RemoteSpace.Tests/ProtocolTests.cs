using RemoteSpace.Core;
using RemoteSpace.Protocol;
using RemoteSpace.Graphics;

internal static class ProtocolTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        byte[] rgb = BitmapDecoder.Decode(new byte[] { 0, 0, 255, 0 }, 1, 1, 24, false);
        check(rgb.SequenceEqual(new byte[] { 255, 0, 0, 255 }), "24-bit BGR to RGBA");
        check(BitmapDecoder.Decode(new byte[] { 0, 248, 0, 0 }, 1, 1, 16, false)[0] == 255, "RGB565 expansion");
        var twoRows = BitmapDecoder.Decode(new byte[] { 0, 0, 255, 0, 255, 0, 0, 0 }, 1, 2, 24, false);
        check(twoRows[2] == 255 && twoRows[4] == 255, "bottom-up rows are inverted");
        check(BitmapDecoder.Decode(new byte[] { 0x64, 0x56, 0x34, 0x12 }, 2, 2, 24, true).Where((_, i) => i % 4 == 0).All(b => b == 0x12), "RLE regular color run");
        check(BitmapDecoder.Decode(new byte[] { 0xe2, 0, 0, 255, 0, 255, 0 }, 4, 1, 24, true).SequenceEqual(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 255, 0, 0, 255, 0, 255, 0, 255 }), "RLE dither pairs");
        check(BitmapDecoder.Decode(new byte[] { 0xfd, 0xfe }, 2, 1, 24, true)[0] == 255, "RLE special white and black");
        check(BitmapDecoder.Decode(new byte[] { 0x41, 0x81 }, 8, 1, 24, true)[28] == 255, "RLE mask little-endian bits");
        check(BitmapDecoder.Decode(new byte[] { 0xc4, 1, 2, 3 }, 4, 1, 24, true)[0] == 3, "RLE set foreground run");
        check(BitmapDecoder.Decode(new byte[] { 0xf3, 1, 0, 9, 8, 7 }, 1, 1, 24, true)[0] == 7, "RLE mega color run");
        reject(() => BitmapDecoder.Decode(new byte[] { 0x7f, 1, 2, 3 }, 1, 1, 24, true), "RLE output overflow rejected");
        reject(() => BitmapDecoder.Decode(new byte[] { 0x61, 1 }, 1, 1, 24, true), "RLE truncated pixel rejected");
        reject(() => BitmapDecoder.Decode(new byte[] { 0xfb }, 1, 1, 24, true), "RLE reserved opcode rejected");
        reject(() => BitmapDecoder.Decode(new byte[] { 0 }, 1, 1, 24, false), "bitmap padding validated");
        byte[] content = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        var assembler = new ChannelAssembler(); byte[]? assembled = null;
        foreach (byte[] chunk in ChannelAssembler.Fragment(content)) assembled = assembler.Accept(chunk);
        check(assembled!.SequenceEqual(content), "virtual channel fragmentation round trip");
        reject(() => new ChannelAssembler().Accept(new PacketWriter().U32(1).U32(2).U8(0).ToArray()), "orphan virtual-channel last fragment");
        var empty = new ChannelAssembler().Accept(ChannelAssembler.Fragment([]).Single()); check(empty!.Length == 0, "empty virtual channel payload");
        foreach (bool longNames in new[] { false, true }) check(ClipboardCodec.HasUnicodeText(ClipboardCodec.Parse(ClipboardCodec.FormatList(longNames)).Data, longNames), "clipboard format list encoding");
        check(ClipboardCodec.ReadText(ClipboardCodec.Parse(ClipboardCodec.TextResponse("Zażółć 😀")).Data) == "Zażółć 😀", "clipboard UTF16 non-BMP round trip");
        check(ClipboardCodec.SupportsLongNames(ClipboardCodec.Parse(ClipboardCodec.Capabilities()).Data), "clipboard long format capability");
        reject(() => ClipboardCodec.ReadText(new byte[] { 65, 0 }), "clipboard terminator required");
        reject(() => ClipboardCodec.TextResponse("a\0b"), "clipboard embedded NUL rejected");
        var initial = Mcs.ConnectInitial(new ConnectionOptions { Host = "localhost", RedirectClipboard = true }, SecurityProtocols.Tls);
        check(new PacketReader(initial).Ber(0x7f65).Length > 300, "MCS connect initial and GCC generated");
        check(Mcs.ParseAttachConfirm(new byte[] { 0x2e, 0, 0, 1 }) == 1002, "MCS constrained user ID");
        Mcs.ParseJoinConfirm(new byte[] { 0x3e, 0, 0, 1, 3, 235, 3, 235 }, 1002, 1003); check(true, "MCS channel join response");
        var demand = new DemandActive(0x123456, 1280, 720, 24);
        var confirm = Activation.ParseShares(Activation.Confirm(demand, 1004, 0x409)).Single();
        check(confirm.Type == 3 && confirm.Source == 1004, "confirm active framing");
        var keyboard = Activation.ParseData(Activation.ParseShares(Activation.Input(demand.ShareId, 1004, new RemoteInput(InputKind.ScanCode, 0x8000, 30))).Single().Body);
        check(keyboard.Type == 28 && keyboard.Body.Length == 16, "slow-path input event wire size");
        var fast = new FastPathAssembler(); byte[]? combined = null;
        fast.Accept(new PacketWriter().U8(0x21).U16(2).Bytes([1, 2]).ToArray(), (_, b) => combined = b);
        fast.Accept(new PacketWriter().U8(0x11).U16(2).Bytes([3, 4]).ToArray(), (_, b) => combined = b);
        check(combined!.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "fast-path first/last reassembly");
        reject(() => new FastPathAssembler().Accept(new byte[] { 0x11, 0, 0 }, (_, _) => { }), "orphan fast-path fragment rejected");
    }
}
