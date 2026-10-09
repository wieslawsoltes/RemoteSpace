using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteSpace.Core;
using RemoteSpace.Protocol;
using RemoteSpace.Graphics;
using RemoteSpace.Security;

namespace RemoteSpace.Client;

public sealed partial class RdpSession
{
    private async ValueTask ProcessGlobalAsync(byte[] payload, CancellationToken ct)
    {
        if (_desktop is null && payload.Length >= 4) Log("Establishing", $"Global-channel envelope: {payload.Length} bytes, header 0x{BinaryPrimitives.ReadUInt32LittleEndian(payload):X8}.");
        if (_licensing is { IsCompleted: false } && RemoteSpace.Licensing.LicenseClient.IsLicensePacket(payload))
        {
            var result = (_licensing ?? throw new RdpProtocolException("Licensing packet before TLS setup.")).Accept(payload);
            if (result.Reply is not null) await SendChannelAsync(_global, result.Reply, ct);
            if (result.Completed) Log("Licensing", "Server accepted the client license exchange.");
            if (result.Issued is not null) Log("Licensing", "Server issued a license; persistent CAL storage is not configured.");
            return;
        }
        foreach (var share in Activation.ParseShares(payload))
        {
            switch (share.Type)
            {
                case 1:
                    _desktop = Activation.ParseDemand(share.Body); SetState(SessionState.Establishing);
                    DesktopResized?.Invoke(new(_desktop.Width, _desktop.Height));
                    await SendShareAsync(Activation.Confirm(_desktop, _user, _options!.KeyboardLayout), ct);
                    await SendShareAsync(Activation.Synchronize(_desktop.ShareId, _user, share.Source), ct);
                    await SendShareAsync(Activation.Control(_desktop.ShareId, _user, 4), ct);
                    await SendShareAsync(Activation.Control(_desktop.ShareId, _user, 1), ct);
                    await SendShareAsync(Activation.FontList(_desktop.ShareId, _user), ct);
                    break;
                case 6: SetState(SessionState.Establishing); Log("Session", "Server deactivated the desktop; awaiting reactivation."); break;
                case 7:
                    var data = Activation.ParseData(share.Body);
                    if (_desktop is null || data.ShareId != _desktop.ShareId) throw new RdpProtocolException("Share data referenced an inactive desktop.");
                    switch (data.Type)
                    {
                        case 2: SlowUpdate(data.Body); break;
                        case 27: SlowPointer(data.Body); break;
                        case 40:
                            SetState(SessionState.Active); Log("Session", $"Desktop active: {_desktop.Width}×{_desktop.Height}, {_desktop.BitsPerPixel}-bit bitmap updates.");
                            await SendShareAsync(Activation.Input(_desktop.ShareId, _user, new(InputKind.Synchronize, 0, 0)), ct);
                            await SendShareAsync(Activation.Refresh(_desktop.ShareId, _user, _desktop.Width, _desktop.Height), ct); break;
                        case 47:
                            var e = new PacketReader(data.Body); uint error = e.U32(); e.End();
                            if (error != 0) throw new RdpProtocolException($"Server RDP error information: 0x{error:X8}."); break;
                        case 20: case 31: case 38: case 37: break;
                        default: Log("Protocol", $"Received optional share data type {data.Type}; no handler is installed."); break;
                    }
                    break;
                default: throw new RdpProtocolException($"Unsupported share control PDU {share.Type}.");
            }
        }
    }
}
