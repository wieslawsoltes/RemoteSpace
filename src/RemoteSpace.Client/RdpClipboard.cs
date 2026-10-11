using RemoteSpace.Core;
using RemoteSpace.Protocol;

namespace RemoteSpace.Client;

public sealed partial class RdpSession
{
    private readonly TextClipboardChannel _clipboard = new();
    private readonly SemaphoreSlim _clipboardWriter = new(1, 1);
    private async ValueTask SendClipboardRepliesAsync(ClipboardChannelResult result, CancellationToken ct)
    {
        try
        {
            foreach (byte[] reply in result.Replies)
            {
                var header = new PacketReader(reply);
                ushort type = header.U16(), flags = header.U16(); uint length = header.U32();
                Log("Clipboard", $"Sending type {type}, flags {flags}, {length} payload bytes.");
                foreach (byte[] fragment in ChannelAssembler.Fragment(reply))
                    await SendChannelAsync(_clipboardChannel, fragment, ct);
            }
        }
        catch
        {
            // A cancelled partial static-channel message cannot safely be followed
            // by a new FIRST fragment. End the session instead of corrupting the peer.
            StopTransport(); throw;
        }
    }
    private async ValueTask ProcessClipboardAsync(ClipboardPacket packet, CancellationToken ct)
    {
        ClipboardChannelResult result;
        await _clipboardWriter.WaitAsync(ct);
        try
        {
            Log("Clipboard", $"Received type {packet.Type}, flags {packet.Flags}, {packet.Data.Length} payload bytes.");
            result = _clipboard.Accept(packet);
            await SendClipboardRepliesAsync(result, ct);
        }
        finally { _clipboardWriter.Release(); }
        if (result.TextChanged) ClipboardReceived?.Invoke(result.Text ?? "");
        if (result.Diagnostic is not null) Log("Clipboard", result.Diagnostic);
    }
    private async ValueTask ResetClipboardAsync()
    {
        await _clipboardWriter.WaitAsync();
        try { _clipboard.Reset(); }
        finally { _clipboardWriter.Release(); }
    }
    public async ValueTask SendClipboardAsync(string text, CancellationToken cancellationToken = default)
    {
        await _clipboardWriter.WaitAsync(cancellationToken);
        try
        {
            if (State != SessionState.Active || !_clipboard.IsReady || _clipboardChannel == 0)
                throw new InvalidOperationException("Clipboard redirection was not enabled or is not ready.");
            var result = _clipboard.OfferText(text);
            Log("Clipboard", $"Offered {text.Length} text characters; {result.Replies.Length} immediate protocol replies.");
            await SendClipboardRepliesAsync(result, cancellationToken);
        }
        finally { _clipboardWriter.Release(); }
    }
}
