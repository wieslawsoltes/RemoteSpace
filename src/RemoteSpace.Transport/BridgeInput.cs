using RemoteSpace.Core;

namespace RemoteSpace.Transport;

public sealed partial class BridgeSession : IRemoteInputBatch, IRemoteInputCapabilities
{
    private bool _inputBatches;
    public InputCapabilities? InputCapabilities { get; private set; }
    public event Action<InputCapabilities?>? InputCapabilitiesChanged;
    public ValueTask SendInputsAsync(ReadOnlyMemory<RemoteInput> inputs, CancellationToken cancellationToken = default)
    {
        if (inputs.Length is < 1 or > RemoteInputValidation.MaximumBatch) throw new ArgumentOutOfRangeException(nameof(inputs));
        if (State != SessionState.Active) throw new InvalidOperationException("The remote desktop is not active.");
        foreach (var input in inputs.Span) RemoteInputValidation.Validate(input, InputCapabilities);
        // Own the snapshot across all awaits, including the compatibility path.
        var snapshot = inputs.ToArray();
        return _inputBatches ? SendAsync(BridgeWire.Control(new() { Type = "input-batch", Inputs = snapshot }), cancellationToken)
            : SendLegacyAsync(snapshot, cancellationToken);
    }
    private async ValueTask SendLegacyAsync(RemoteInput[] inputs, CancellationToken ct)
    {
        // Serialize the whole batch, not just individual events, on an older bridge.
        await _writer.WaitAsync(ct);
        try
        {
            foreach (var input in inputs)
                await _socket.SendAsync(BridgeWire.Control(new() { Type = "input", Input = input }).AsMemory(), System.Net.WebSockets.WebSocketMessageType.Text, true, ct);
        }
        catch { _stop.Cancel(); _socket.Abort(); throw; }
        finally { _writer.Release(); }
    }
}
