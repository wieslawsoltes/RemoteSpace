using RemoteSpace.Core;

namespace RemoteSpace.Transport;

public sealed partial class BridgeSession : IRemoteDisplayControl
{
    public DisplayCapabilities? DisplayCapabilities { get; private set; }
    public event Action<DisplayCapabilities?>? DisplayCapabilitiesChanged;
    public ValueTask RequestDisplayLayoutAsync(IReadOnlyList<DisplayMonitor> monitors, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count > DisplayLayout.MaximumMonitors) throw new ArgumentException("Too many monitors.", nameof(monitors));
        if (State != SessionState.Active || DisplayCapabilities is not { } capabilities) throw new InvalidOperationException("Display control was not negotiated.");
        var snapshot = monitors.ToArray(); DisplayLayout.Validate(snapshot, capabilities);
        return SendAsync(BridgeWire.Control(new() { Type = "display-layout", Monitors = snapshot }), cancellationToken);
    }
}
