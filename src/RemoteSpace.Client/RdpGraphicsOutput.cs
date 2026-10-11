using RemoteSpace.Core;
using RemoteSpace.Protocol;

namespace RemoteSpace.Client;

public sealed partial class RdpSession : IRemoteGraphicsOutput
{
    private readonly SemaphoreSlim _graphicsWriter = new(1, 1);
    public GraphicsOutputCapabilities? GraphicsOutputCapabilities { get; private set; }
    public event Action<GraphicsOutputCapabilities?>? GraphicsOutputCapabilitiesChanged;
    public bool OutputEnabled { get; private set; } = true;
    public event Action<bool>? OutputEnabledChanged;
    private void ConfigureGraphicsOutput(byte[] demand)
    {
        GraphicsOutputCapabilities = GraphicsOutputCodec.ReadCapabilities(demand);
        GraphicsOutputCapabilitiesChanged?.Invoke(GraphicsOutputCapabilities);
    }
    public async ValueTask SetOutputEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _graphicsWriter.WaitAsync(cancellationToken);
        try
        {
            var desktop = RequireGraphicsOutput(false);
            await SendShareAsync(GraphicsOutputCodec.Suppress(desktop.ShareId, _user, enabled, desktop.Width, desktop.Height), cancellationToken);
            OutputEnabled = enabled; OutputEnabledChanged?.Invoke(enabled);
            Log("GraphicsOutput", enabled ? "Graphics output resume requested." : "Graphics output pause requested.");
        }
        finally { _graphicsWriter.Release(); }
    }
    public async ValueTask RequestRefreshAsync(CancellationToken cancellationToken = default)
    {
        await _graphicsWriter.WaitAsync(cancellationToken);
        try
        {
            var desktop = RequireGraphicsOutput(true);
            await SendShareAsync(GraphicsOutputCodec.Refresh(desktop.ShareId, _user, desktop.Width, desktop.Height), cancellationToken);
            Log("GraphicsOutput", "Desktop refresh requested.");
        }
        finally { _graphicsWriter.Release(); }
    }
    private DemandActive RequireGraphicsOutput(bool refresh)
    {
        if (State != SessionState.Active || _desktop is null || GraphicsOutputCapabilities is not { } caps ||
            (refresh ? !caps.RefreshRect : !caps.SuppressOutput))
            throw new InvalidOperationException("The active server did not negotiate this graphics-output control.");
        return _desktop;
    }
    private async ValueTask RestoreGraphicsOutputAsync(CancellationToken ct)
    {
        await _graphicsWriter.WaitAsync(ct);
        try
        {
            if (_desktop is not { } desktop || GraphicsOutputCapabilities is not { } caps) return;
            if (!OutputEnabled && caps.SuppressOutput)
                await SendShareAsync(GraphicsOutputCodec.Suppress(desktop.ShareId, _user, false, desktop.Width, desktop.Height), ct);
            else
            {
                OutputEnabled = true; OutputEnabledChanged?.Invoke(true);
                if (caps.RefreshRect) await SendShareAsync(GraphicsOutputCodec.Refresh(desktop.ShareId, _user, desktop.Width, desktop.Height), ct);
            }
        }
        finally { _graphicsWriter.Release(); }
    }
}
