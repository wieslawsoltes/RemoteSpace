using RemoteSpace.Core;
using RemoteSpace.Protocol;

namespace RemoteSpace.Client;

public sealed partial class RdpSession : IRemoteInputBatch, IRemoteInputCapabilities
{
    public InputCapabilities? InputCapabilities { get; private set; }
    public event Action<InputCapabilities?>? InputCapabilitiesChanged;
    public ValueTask SendInputAsync(RemoteInput input, CancellationToken cancellationToken = default) => SendInputsAsync(new[] { input }, cancellationToken);
    public ValueTask SendInputsAsync(ReadOnlyMemory<RemoteInput> inputs, CancellationToken cancellationToken = default)
    {
        if (inputs.Length is < 1 or > RemoteInputValidation.MaximumBatch) throw new ArgumentOutOfRangeException(nameof(inputs));
        var desktop = _desktop;
        if (State != SessionState.Active || desktop is null) throw new InvalidOperationException("The remote desktop is not active.");
        foreach (var input in inputs.Span)
        {
            RemoteInputValidation.Validate(input, desktop.InputCapabilities);
            if (RemoteInputValidation.IsPositioned(input) && (input.X >= desktop.Width || input.Y >= desktop.Height))
                throw new ArgumentOutOfRangeException(nameof(inputs), "Input coordinates exceed the current desktop.");
        }
        // Encode all events before yielding. A caller cannot mutate an awaiting send.
        return SendShareAsync(Activation.Inputs(desktop.ShareId, _user, inputs.Span), cancellationToken);
    }
}
