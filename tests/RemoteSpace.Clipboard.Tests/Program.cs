using RemoteSpace.Protocol;

int count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED " + name); count++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name)
{
    bool failed = false;
    try { action(); } catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException) { failed = true; }
    Check(failed, name);
}
ClipboardPacket Packet(byte[] pdu) => ClipboardCodec.Parse(pdu);
ClipboardPacket Ack(bool ok = true) => new(3, (ushort)(ok ? 1 : 2), []);
ClipboardPacket Request(uint format = 13) => new(4, 0, new PacketWriter().U32(format).ToArray());
TextClipboardChannel Ready(bool longNames = true)
{
    var channel = new TextClipboardChannel();
    if (longNames) channel.Accept(Packet(ClipboardCodec.Capabilities()));
    var startup = channel.Accept(new(1,0,[]));
    Check(startup.Replies.Length == 2 && Packet(startup.Replies[0]).Type == 7 && Packet(startup.Replies[1]).Data.Length == 0, "initial capability and empty format-list handshake");
    channel.Accept(Ack()); return channel;
}
Reject(() => new TextClipboardChannel().OfferText("early"), "offer before monitor-ready rejected");
Reject(() => new TextClipboardChannel().Accept(Request()), "peer data request before readiness rejected");
foreach (bool longNames in new[] { false, true })
{
    var c = Ready(longNames);
    string text = "Zażółć 日本語 العربية 🙂";
    var offer = c.OfferText(text);
    Check(offer.Replies.Length == 1 && ClipboardCodec.HasUnicodeText(Packet(offer.Replies[0]).Data, longNames), "offer uses negotiated format-list encoding");
    Check(ClipboardCodec.ReadText(Packet(c.Accept(Request()).Replies.Single()).Data) == text, "serves advertised Unicode snapshot");
    Check(Packet(c.Accept(Request(1)).Replies.Single()).Flags == 2, "unadvertised format receives failure");
    Check(c.OfferText("second").Replies.Length == 0 && c.OfferText("latest").Replies.Length == 0, "new local offers coalesce while awaiting acknowledgement");
    Check(ClipboardCodec.ReadText(Packet(c.Accept(Request()).Replies.Single()).Data) == text, "pending local copy does not change advertised snapshot");
    Check(c.Accept(Ack()).Replies.Length == 1, "acknowledgement sends one coalesced replacement");
    Check(ClipboardCodec.ReadText(Packet(c.Accept(Request()).Replies.Single()).Data) == "latest", "coalesced offer contains latest text");
    c.Accept(Ack());
    Check(!c.HasPendingOffer, "last acknowledgement drains offers");
    c.OfferText(""); c.Accept(Ack());
    Check(ClipboardCodec.ReadText(Packet(c.Accept(Request()).Replies.Single()).Data) == "", "empty clipboard text remains a valid offered format");
    c.OfferText("rejected");
    Check(c.Accept(Ack(false)).Diagnostic is not null && Packet(c.Accept(Request()).Replies.Single()).Flags == 2, "failed offer is diagnosed and not served");
}
var peer = Ready();
var formats = Packet(ClipboardCodec.FormatList(true));
Check(peer.Accept(formats).Replies.Select(p => Packet(p).Type).SequenceEqual(new ushort[] {3,4}), "remote offer acknowledged then text requested");
Check(peer.Accept(formats).Replies.Length == 1, "no second simultaneous request when remote clipboard changes");
var stale = peer.Accept(Packet(ClipboardCodec.TextResponse("obsolete")));
Check(!stale.TextChanged && stale.Replies.Length == 1 && peer.HasPendingRequest, "obsolete response discarded and latest generation requested");
var current = peer.Accept(Packet(ClipboardCodec.TextResponse("current")));
Check(current.TextChanged && current.Text == "current" && !peer.HasPendingRequest, "latest response delivered exactly once");
peer.Accept(formats); peer.OfferText("local owner");
Check(!peer.Accept(Packet(ClipboardCodec.TextResponse("old remote"))).TextChanged, "local copy invalidates outstanding remote response");
peer.Accept(Ack()); peer.Accept(formats);
var cleared = peer.Accept(new(2,0,[]));
Check(cleared.TextChanged && cleared.Text == "", "remote empty format list clears previously cached text");
Check(!peer.Accept(Packet(ClipboardCodec.TextResponse("before clear"))).TextChanged, "pre-clear response cannot resurrect stale remote text");
peer.Accept(formats);
var failure = peer.Accept(new(5,2,[]));
Check(failure.TextChanged && failure.Text == "" && failure.Diagnostic is not null, "failed remote read clears stale cache and reports failure");
Reject(() => peer.Accept(new(5,1,[0,0])), "unsolicited data response rejected");
Reject(() => peer.Accept(new(1,0,[])), "duplicate readiness rejected");
Reject(() => peer.Accept(new(3,3,[])), "contradictory response flags rejected");
Reject(() => peer.OfferText("contains\0nul"), "local NUL rejected without altering state");
Reject(() => peer.OfferText("\ud800"), "unpaired local surrogate rejected");
Reject(() => peer.OfferText(new string('x', 524288)), "text allocation limit enforced");
peer.Reset(); Check(!peer.IsReady && !peer.HasPendingRequest && !peer.HasPendingOffer, "reset drops cached texts and protocol state");
Console.WriteLine($"RESULT: {count} clipboard state checks passed.");
