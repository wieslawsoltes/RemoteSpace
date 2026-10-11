namespace RemoteSpace.Protocol;

public sealed record ClipboardChannelResult(byte[][] Replies, bool TextChanged = false, string? Text = null, string? Diagnostic = null);

/// <summary>Bounded MS-RDPECLIP Unicode-text endpoint. Serial callers own sending the returned
/// PDUs in order. Coalesces local offers while awaiting acknowledgement and uses generations
/// to discard obsolete remote responses. It never reads or writes a system clipboard itself.</summary>
public sealed class TextClipboardChannel
{
    private bool _ready, _longNames, _offerPending, _offerDirty, _remoteHasText;
    private string? _localText, _advertisedText;
    private long _generation;
    private long? _requestGeneration;
    public bool IsReady => _ready;
    public bool HasPendingRequest => _requestGeneration is not null;
    public bool HasPendingOffer => _offerPending;

    public ClipboardChannelResult OfferText(string text)
    {
        // Validate before changing state, even while an earlier offer is pending.
        ClipboardCodec.TextResponse(text);
        if (!_ready) throw new InvalidOperationException("Clipboard redirection is not ready.");
        _localText = text; _offerDirty = true; _generation++; _remoteHasText = false;
        return new(_offerPending ? [] : [SendOffer()]);
    }
    public ClipboardChannelResult Accept(ClipboardPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var replies = new List<byte[]>();
        switch (packet.Type)
        {
            case 7:
                RequireFlags(packet, 0); _longNames = ClipboardCodec.SupportsLongNames(packet.Data);
                return new([]);
            case 1:
                RequireFlags(packet, 0); RequireEmpty(packet);
                if (_ready) throw new RdpProtocolException("Duplicate clipboard monitor-ready message.");
                _ready = true; _offerDirty = true;
                return new([ClipboardCodec.Capabilities(), SendOffer()]);
        }
        if (!_ready) throw new RdpProtocolException("Clipboard operation before monitor-ready.");
        switch (packet.Type)
        {
            case 2:
                if ((packet.Flags & ~4) != 0) throw new RdpProtocolException("Invalid clipboard format-list flags.");
                _remoteHasText = ClipboardCodec.HasUnicodeText(packet.Data, _longNames); _generation++;
                replies.Add(ClipboardCodec.Pdu(3, 1, []));
                if (_remoteHasText && _requestGeneration is null) replies.Add(RequestText());
                // Never leave previously copied text available after the remote owner clears it.
                return new(replies.ToArray(), !_remoteHasText, _remoteHasText ? null : "");
            case 3:
                RequireResponse(packet); RequireEmpty(packet);
                if (!_offerPending) throw new RdpProtocolException("Unsolicited clipboard format-list response.");
                _offerPending = false;
                if (packet.Flags == 2) _advertisedText = null;
                if (_offerDirty) replies.Add(SendOffer());
                return new(replies.ToArray(), Diagnostic: packet.Flags == 2 ? "The remote clipboard rejected the offered text format." : null);
            case 4:
                RequireFlags(packet, 0);
                var request = new PacketReader(packet.Data); uint format = request.U32(); request.End();
                // Serve the snapshot advertised in the most recent sent list, not an
                // unsent/coalesced replacement. Never expose text that was not offered.
                return new([format == 13 && _advertisedText is not null ? ClipboardCodec.TextResponse(_advertisedText) : ClipboardCodec.Pdu(5, 2, [])]);
            case 5:
                RequireResponse(packet);
                if (_requestGeneration is not { } requested) throw new RdpProtocolException("Unsolicited clipboard data response.");
                string? text = null;
                if (packet.Flags == 1) text = ClipboardCodec.ReadText(packet.Data); else RequireEmpty(packet);
                _requestGeneration = null;
                bool current = requested == _generation;
                if (!current && _remoteHasText) replies.Add(RequestText());
                return new(replies.ToArray(), current, current ? text ?? "" : null,
                    current && packet.Flags == 2 ? "The remote clipboard could not supply the requested text." : null);
            default:
                return new([], Diagnostic: $"Clipboard message {packet.Type} is not supported; file and rich-format redirection were not negotiated.");
        }
    }
    public void Reset()
    {
        _ready = _longNames = _offerPending = _offerDirty = _remoteHasText = false;
        _localText = _advertisedText = null; _generation = 0; _requestGeneration = null;
    }
    private byte[] SendOffer()
    {
        _advertisedText = _localText; _offerPending = true; _offerDirty = false;
        return _advertisedText is null ? ClipboardCodec.Pdu(2, 0, []) : ClipboardCodec.FormatList(_longNames);
    }
    private byte[] RequestText()
    {
        _requestGeneration = _generation;
        return ClipboardCodec.Pdu(4, 0, new PacketWriter().U32(13).ToArray());
    }
    private static void RequireFlags(ClipboardPacket packet, ushort flags)
    {
        if (packet.Flags != flags) throw new RdpProtocolException("Invalid clipboard message flags.");
    }
    private static void RequireResponse(ClipboardPacket packet)
    {
        if (packet.Flags is not (1 or 2)) throw new RdpProtocolException("Clipboard response must indicate exactly success or failure.");
    }
    private static void RequireEmpty(ClipboardPacket packet)
    {
        if (packet.Data.Length != 0) throw new RdpProtocolException("Unexpected clipboard response payload.");
    }
}
