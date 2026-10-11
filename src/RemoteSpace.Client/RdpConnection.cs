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
    public async Task RunAsync(ConnectionOptions options, SessionCredentials credentials, CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("Create a new session object to reconnect.");
        _options = options;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        handshake.CancelAfter(options.ConnectTimeout);
        try
        {
            SetState(SessionState.Connecting); _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(options.Host, options.Port, handshake.Token); _stream = _tcp.GetStream();
            SetState(SessionState.Negotiating);
            var requested = options.Security == SecurityMode.NetworkLevelAuthentication ? SecurityProtocols.Hybrid : SecurityProtocols.Tls;
            await WriteAsync(Negotiation.Request(requested), handshake.Token);
            var confirm = await Framing.ReadAsync(_stream, handshake.Token);
            if (confirm.IsFastPath) throw new RdpProtocolException("Fast-path packet during security negotiation.");
            var selected = Negotiation.ParseConfirm(confirm.Payload, requested);
            SetState(SessionState.Securing);
            var tls = new SslStream(_stream, false, (_, certificate, _, errors) =>
            {
                bool valid = CertificatePolicy.Validate(certificate, errors, options.CertificateSha256, DateTimeOffset.UtcNow);
                if (!valid && certificate is not null) Log("Certificate", "Certificate validation failed. Verify its SHA-256 fingerprint out of band: " + certificate.GetCertHashString(HashAlgorithmName.SHA256));
                return valid;
            });
            _stream = tls;
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = options.Host, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.Online, AllowTlsResume = false
            }, handshake.Token);
            Log("TLS", $"Authenticated {tls.SslProtocol}; cipher {tls.NegotiatedCipherSuite}.");
            if (selected.Protocol == SecurityProtocols.Hybrid)
            {
                SetState(SessionState.Authenticating);
                await CredSsp.AuthenticateAsync(tls, options.Host, credentials, handshake.Token);
                Log("CredSSP", "Modern channel binding verified; credentials delegated over the protected channel.");
            }
            SetState(SessionState.Establishing);
            _licensing = new RemoteSpace.Licensing.LicenseClient(credentials.UserName, Environment.MachineName, tls.IsAuthenticated && tls.IsEncrypted);
            await WriteMcsAsync(Mcs.ConnectInitial(options, selected.Protocol), handshake.Token);
            var names = Mcs.StaticChannelNames(options);
            var settings = Mcs.ParseConnectResponse(await ReadMcsAsync(handshake.Token), names.Length);
            if (settings.RequestedProtocols is { } protocols && protocols != (uint)requested) throw new RdpProtocolException("Server's security negotiation echo did not match.");
            _global = settings.GlobalChannel;
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == "cliprdr") _clipboardChannel = settings.StaticChannels[i];
                if (names[i] == "drdynvc") _dynamicChannel = settings.StaticChannels[i];
            }
            await WriteMcsAsync(Mcs.ErectDomain(), handshake.Token);
            await WriteMcsAsync(Mcs.AttachUser(), handshake.Token);
            _user = Mcs.ParseAttachConfirm(await ReadMcsAsync(handshake.Token));
            foreach (ushort channel in new[] { _user, _global }.Concat(settings.StaticChannels).Distinct())
            {
                await WriteMcsAsync(Mcs.Join(_user, channel), handshake.Token);
                Mcs.ParseJoinConfirm(await ReadMcsAsync(handshake.Token), _user, channel);
            }
            byte[] info = Activation.ClientInfo(options, credentials, selected.Protocol == SecurityProtocols.Hybrid);
            try { await SendChannelAsync(_global, info, handshake.Token); }
            finally { CryptographicOperations.ZeroMemory(info); }
            while (!lifetime.IsCancellationRequested)
            {
                bool wasActive = State == SessionState.Active;
                CancellationToken ct = State == SessionState.Active ? lifetime.Token : handshake.Token;
                var packet = await Framing.ReadAsync(_stream, ct); ReceivedBytes += packet.Payload.Length;
                if (packet.IsFastPath) _fastPath.Accept(packet.Payload, FastUpdate);
                else
                {
                    byte[] mcs = Framing.UnwrapData(packet.Payload);
                    if (mcs.Length > 0 && (mcs[0] & 0xfc) == 0x20) { Log("Session", "Server disconnected the transport."); break; }
                    var channel = Mcs.ParseData(mcs);
                    if (channel.Channel == _global) await ProcessGlobalAsync(channel.Payload, ct);
                    else if (channel.Channel == _clipboardChannel && _clipboardChannel != 0)
                    {
                        byte[]? clipboard = _clipboardFragments.Accept(channel.Payload);
                        if (clipboard is not null) await ProcessClipboardAsync(ClipboardCodec.Parse(clipboard), ct);
                    }
                    else if (channel.Channel == _dynamicChannel && _dynamicChannel != 0)
                    {
                        byte[]? dynamic = _dynamicFragments.Accept(channel.Payload);
                        if (dynamic is not null) await ProcessDynamicAsync(dynamic, ct);
                    }
                    else throw new RdpProtocolException("Data arrived on an unjoined channel.");
                }
                if (State == SessionState.Active) handshake.CancelAfter(Timeout.InfiniteTimeSpan);
                else if (wasActive) handshake.CancelAfter(options.ConnectTimeout);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) when (lifetime.IsCancellationRequested) { }
        catch (Exception e)
        {
            SetState(SessionState.Faulted); Log("Error", e is OperationCanceledException ? "Connection establishment timed out." : e.Message); throw;
        }
        finally
        {
            try
            {
                if (_stream is not null) await _stream.DisposeAsync();
                if (State != SessionState.Faulted) SetState(SessionState.Disconnected);
            }
            finally { _tcp?.Dispose(); _licensing?.Dispose(); _clipboardFragments.Reset(); _dynamicFragments.Reset(); _fastPath.Reset(); await ResetClipboardAsync(); _dynamicChannels.Reset(); DisplayCapabilities = null; _completed.TrySetResult(); }
        }
    }
}
