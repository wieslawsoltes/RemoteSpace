using RemoteSpace.Core;
using RemoteSpace.Transport;

internal static class BridgeWireTests
{
    internal static void Run(Action<bool,string> check, Action<Action,string> reject)
    {
        string text = "RemoteSpace → Żółć 日本語 Ελληνικά العربية 🙂";
        check(BridgeWire.ParseControl(BridgeWire.Control(new() { Type = "clipboard", Text = text })).Text == text, "bridge preserves multilingual clipboard text and surrogate pairs");
        reject(() => BridgeWire.Control(new() { Type = "clipboard", Text = new string('日', 400_000) }), "escaped Unicode clipboard byte limit is enforced before sending");
        reject(() => BridgeWire.ParseControl(new byte[BridgeWire.MaximumControl + 1]), "direct bridge parser enforces its byte limit before JSON decoding");
        var surface = new SurfaceUpdate(12,34,1,1,[1,2,3,255]);
        var decoded = BridgeWire.ParseSurface(BridgeWire.Surface(surface));
        check(decoded.X == 12 && decoded.Y == 34 && decoded.Rgba.SequenceEqual(surface.Rgba), "browser surface framing preserves dirty origin and RGBA");
        var layout = new[] { new DisplayMonitor(0,0,1280,720), new DisplayMonitor(-1280,0,1280,720,false) };
        var control = BridgeWire.ParseControl(BridgeWire.Control(new() { Type = "display-layout", Monitors = layout }));
        check(control.Monitors!.SequenceEqual(layout), "bridge display layouts preserve signed monitor coordinates");
    }
}
