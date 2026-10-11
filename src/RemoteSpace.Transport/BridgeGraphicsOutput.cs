using RemoteSpace.Core;

namespace RemoteSpace.Transport;

public sealed partial class BridgeSession : IRemoteGraphicsOutput
{
    private readonly SemaphoreSlim _graphicsWriter = new(1, 1);
    private TaskCompletionSource? _outputAck;
    private bool _requestedOutput;
    public GraphicsOutputCapabilities? GraphicsOutputCapabilities { get; private set; }
    public event Action<GraphicsOutputCapabilities?>? GraphicsOutputCapabilitiesChanged;
    public bool OutputEnabled { get; private set; } = true;
    public event Action<bool>? OutputEnabledChanged;
    public async ValueTask SetOutputEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _graphicsWriter.WaitAsync(cancellationToken);
        try
        {
            if (State != SessionState.Active || GraphicsOutputCapabilities is not { SuppressOutput: true })
                throw new InvalidOperationException("Graphics-output suppression was not negotiated.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            _requestedOutput = enabled; _outputAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await SendAsync(BridgeWire.Control(new() { Type = "graphics-output", OutputEnabled = enabled }), deadline.Token);
                await _outputAck.Task.WaitAsync(deadline.Token);
            }
            finally { _outputAck = null; }
        }
        finally { _graphicsWriter.Release(); }
    }
    public ValueTask RequestRefreshAsync(CancellationToken cancellationToken = default)
    {
        if (State != SessionState.Active || GraphicsOutputCapabilities is not { RefreshRect: true })
            throw new InvalidOperationException("Desktop refresh was not negotiated.");
        return SendAsync(BridgeWire.Control(new() { Type = "graphics-refresh" }), cancellationToken);
    }
}
