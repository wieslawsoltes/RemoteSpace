using RemoteSpace.Channels;
using RemoteSpace.Core;
using RemoteSpace.Protocol;

namespace RemoteSpace.Client;

public sealed partial class RdpSession : IRemoteDisplayControl
{
    private readonly ChannelAssembler _dynamicFragments = new();
    private readonly DynamicChannels _dynamicChannels = new([DisplayControlCodec.ChannelName]);
    private readonly SemaphoreSlim _dynamicWriter = new(1, 1);
    private ushort _dynamicChannel;
    private uint? _displayChannelId;
    public DisplayCapabilities? DisplayCapabilities { get; private set; }
    public event Action<DisplayCapabilities?>? DisplayCapabilitiesChanged;
    private async ValueTask SendDynamicPacketAsync(byte[] packet, CancellationToken ct)
    {
        try { foreach (var part in ChannelAssembler.Fragment(packet)) await SendChannelAsync(_dynamicChannel, part, ct); }
        catch { StopTransport(); throw; } // A partially sent static-channel message cannot be resumed safely.
    }
    private async ValueTask ProcessDynamicAsync(byte[] packet, CancellationToken ct)
    {
        await _dynamicWriter.WaitAsync(ct);
        try
        {
            var result = _dynamicChannels.Accept(packet);
            if (result.Event is { Kind: DynamicChannelEventKind.Opened } && _displayChannelId is not null)
                throw new RdpProtocolException("A second display-control channel was requested.");
            foreach (var reply in result.Replies) await SendDynamicPacketAsync(reply, ct);
            if (result.Event is not { } message) return;
            switch (message.Kind)
            {
                case DynamicChannelEventKind.Opened:
                    _displayChannelId = message.Id; Log("Display", "Display-control channel opened; awaiting server capabilities."); break;
                case DynamicChannelEventKind.Data:
                    DisplayCapabilities = DisplayControlCodec.ParseCapabilities(message.Data);
                    DisplayCapabilitiesChanged?.Invoke(DisplayCapabilities);
                    Log("Display", $"Dynamic resolution available; at most {DisplayCapabilities.MaximumMonitors} monitors and {DisplayCapabilities.MaximumArea} pixels."); break;
                case DynamicChannelEventKind.Closed:
                    _displayChannelId = null; DisplayCapabilities = null; DisplayCapabilitiesChanged?.Invoke(null); break;
            }
        }
        finally { _dynamicWriter.Release(); }
    }
    private async ValueTask ResetDynamicAsync()
    {
        await _dynamicWriter.WaitAsync();
        try { _dynamicChannels.Reset(); _displayChannelId = null; DisplayCapabilities = null; }
        finally { _dynamicWriter.Release(); }
    }
    public async ValueTask RequestDisplayLayoutAsync(IReadOnlyList<DisplayMonitor> monitors, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count > DisplayLayout.MaximumMonitors) throw new ArgumentException("Too many monitors.", nameof(monitors));
        var snapshot = monitors.ToArray();
        await _dynamicWriter.WaitAsync(cancellationToken);
        try
        {
            if (State != SessionState.Active || _displayChannelId is not { } id || DisplayCapabilities is not { } capabilities)
                throw new InvalidOperationException("Dynamic resolution was not negotiated or the desktop is not active.");
            byte[] layout = DisplayControlCodec.MonitorLayout(snapshot, capabilities);
            _dynamicChannels.QueueData(id, layout);
            while (_dynamicChannels.TryDequeue(out var part)) await SendDynamicPacketAsync(part, cancellationToken);
            Log("Display", "Display layout sent. Waiting for the server to apply the requested dimensions.");
        }
        finally { _dynamicWriter.Release(); }
    }
}
