namespace RemoteSpace.Core;

public sealed record GraphicsOutputCapabilities(bool RefreshRect, bool SuppressOutput);

/// <summary>Optional negotiated RDP output control. Completion means the request was
/// sent, not acknowledged by the RDP server (these PDUs have no acknowledgement).
/// Pausing graphics does not pause remote programs, input, or clipboard redirection.</summary>
public interface IRemoteGraphicsOutput
{
    GraphicsOutputCapabilities? GraphicsOutputCapabilities { get; }
    event Action<GraphicsOutputCapabilities?>? GraphicsOutputCapabilitiesChanged;
    bool OutputEnabled { get; }
    event Action<bool>? OutputEnabledChanged;
    ValueTask SetOutputEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    ValueTask RequestRefreshAsync(CancellationToken cancellationToken = default);
}
