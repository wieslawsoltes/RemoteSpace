using System.Buffers;
using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using RemoteSpace.Core;
using RemoteSpace.Protocol;

namespace RemoteSpace.Security;

public sealed record CredSspMessage(int Version, byte[]? Token = null, byte[]? AuthInfo = null, byte[]? PublicKeyAuth = null, uint? Error = null, byte[]? Nonce = null);

/// <summary>Independent MS-CSSP ASN.1 and v5/v6 channel binding. SPNEGO cryptographic mechanisms are supplied by the OS/.NET.</summary>
public static class CredSsp
{
    private static Asn1Tag Tag(int n) => new(TagClass.ContextSpecific, n, true);
    public static byte[] Encode(CredSspMessage message)
    {
        var w = new AsnWriter(AsnEncodingRules.DER); w.PushSequence();
        Integer(w, 0, message.Version);
        if (message.Token is { } token)
        {
            w.PushSequence(Tag(1)); w.PushSequence(); w.PushSequence();
            Octets(w, 0, token); w.PopSequence(); w.PopSequence(); w.PopSequence(Tag(1));
        }
        if (message.AuthInfo is { } auth) Octets(w, 2, auth);
        if (message.PublicKeyAuth is { } key) Octets(w, 3, key);
        if (message.Error is { } error) Integer(w, 4, error);
        if (message.Nonce is { } nonce) Octets(w, 5, nonce);
        w.PopSequence(); return w.Encode();
    }
    private static void Integer(AsnWriter w, int tag, long n) { w.PushSequence(Tag(tag)); w.WriteInteger(n); w.PopSequence(Tag(tag)); }
    private static void Octets(AsnWriter w, int tag, ReadOnlySpan<byte> bytes) { w.PushSequence(Tag(tag)); w.WriteOctetString(bytes); w.PopSequence(Tag(tag)); }
    public static CredSspMessage Decode(ReadOnlyMemory<byte> data)
    {
        if (data.Length > 1_048_576) throw new RdpProtocolException("CredSSP message exceeds the size limit.");
        try
        {
            var outer = new AsnReader(data, AsnEncodingRules.DER); var r = outer.ReadSequence(); outer.ThrowIfNotEmpty();
            var versionField = r.ReadSequence(Tag(0));
            if (!versionField.TryReadInt32(out int version)) throw new RdpProtocolException("Invalid CredSSP version.");
            versionField.ThrowIfNotEmpty();
            byte[]? token = null, auth = null, key = null, nonce = null; uint? error = null; int previous = 0;
            while (r.HasData)
            {
                var tag = r.PeekTag();
                if (tag.TagClass != TagClass.ContextSpecific || tag.TagValue <= previous || tag.TagValue > 5) throw new RdpProtocolException("Invalid or repeated CredSSP field.");
                previous = tag.TagValue; var field = r.ReadSequence(Tag(previous));
                switch (previous)
                {
                    case 1:
                        var tokens = field.ReadSequence(); var item = tokens.ReadSequence(); var value = item.ReadSequence(Tag(0));
                        token = value.ReadOctetString(); value.ThrowIfNotEmpty(); item.ThrowIfNotEmpty(); tokens.ThrowIfNotEmpty(); break;
                    case 2: auth = field.ReadOctetString(); break;
                    case 3: key = field.ReadOctetString(); break;
                    case 4: if (!field.TryReadUInt32(out uint code)) throw new RdpProtocolException("Invalid CredSSP status."); error = code; break;
                    case 5: nonce = field.ReadOctetString(); if (nonce.Length != 32) throw new RdpProtocolException("Invalid CredSSP nonce size."); break;
                }
                field.ThrowIfNotEmpty();
            }
            return new(version, token, auth, key, error, nonce);
        }
        catch (AsnContentException e) { throw new RdpProtocolException("Malformed CredSSP ASN.1: " + e.Message); }
    }
    public static byte[] BindingHash(bool clientToServer, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> publicKey)
    {
        if (nonce.Length != 32 || publicKey.Length is < 1 or > 16384) throw new ArgumentException("Invalid CredSSP binding inputs.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes(clientToServer ? "CredSSP Client-To-Server Binding Hash\0" : "CredSSP Server-To-Client Binding Hash\0"));
        hash.AppendData(nonce); hash.AppendData(publicKey); return hash.GetHashAndReset();
    }
    public static async Task AuthenticateAsync(SslStream tls, string host, SessionCredentials credentials, CancellationToken ct)
    {
        if (!tls.IsAuthenticated || !tls.IsEncrypted || tls.RemoteCertificate is null) throw new AuthenticationException("CredSSP requires an authenticated TLS channel.");
        using var negotiate = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
        {
            Package = "Negotiate", TargetName = "TERMSRV/" + host,
            Credential = new NetworkCredential(credentials.UserName, credentials.Password, credentials.Domain),
            RequiredProtectionLevel = ProtectionLevel.EncryptAndSign,
            AllowedImpersonationLevel = TokenImpersonationLevel.Impersonation
        });
        byte[] publicKey = tls.RemoteCertificate.GetPublicKey();
        byte[] nonce = RandomNumberGenerator.GetBytes(32), incoming = [];
        int version = 6;
        try
        {
            for (int round = 0; round < 16; round++)
            {
                byte[]? outgoing = negotiate.GetOutgoingBlob(incoming.AsSpan(), out var status);
                if (status is not (NegotiateAuthenticationStatusCode.Completed or NegotiateAuthenticationStatusCode.ContinueNeeded))
                    throw new AuthenticationException($"SPNEGO authentication failed: {status}. Check credentials and the platform authentication provider.");
                bool complete = status == NegotiateAuthenticationStatusCode.Completed;
                byte[]? proof = complete ? Wrap(negotiate, BindingHash(true, nonce, publicKey)) : null;
                await tls.WriteAsync(Encode(new(version, outgoing, PublicKeyAuth: proof, Nonce: complete ? nonce : null)), ct);
                CredSspMessage response = Decode(await ReadAsync(tls, ct));
                if (response.Error is > 0) throw new AuthenticationException($"CredSSP rejected authentication (NTSTATUS 0x{response.Error:X8}).");
                if (response.Version < 5) throw new AuthenticationException("The server requires obsolete CredSSP without modern channel binding; downgrade refused.");
                version = Math.Min(version, response.Version);
                if (!complete)
                {
                    incoming = response.Token ?? throw new AuthenticationException("CredSSP server omitted its SPNEGO token.");
                    continue;
                }
                if (response.PublicKeyAuth is null) throw new AuthenticationException("CredSSP server omitted its channel-binding proof.");
                var plain = new ArrayBufferWriter<byte>();
                if (negotiate.Unwrap(response.PublicKeyAuth, plain, out bool encrypted) != NegotiateAuthenticationStatusCode.Completed || !encrypted ||
                    !CryptographicOperations.FixedTimeEquals(plain.WrittenSpan, BindingHash(false, nonce, publicKey)))
                    throw new AuthenticationException("CredSSP server channel binding failed.");
                byte[] password = PasswordCredentials(credentials);
                try { await tls.WriteAsync(Encode(new(version, AuthInfo: Wrap(negotiate, password))), ct); }
                finally { CryptographicOperations.ZeroMemory(password); }
                return;
            }
            throw new AuthenticationException("CredSSP exceeded its authentication round limit.");
        }
        finally { CryptographicOperations.ZeroMemory(nonce); }
    }
    private static byte[] Wrap(NegotiateAuthentication auth, ReadOnlySpan<byte> bytes)
    {
        var output = new ArrayBufferWriter<byte>();
        if (auth.Wrap(bytes, output, true, out bool encrypted) != NegotiateAuthenticationStatusCode.Completed || !encrypted)
            throw new AuthenticationException("The authentication provider did not provide confidentiality.");
        return output.WrittenSpan.ToArray();
    }
    public static byte[] PasswordCredentials(SessionCredentials credentials)
    {
        var w = new AsnWriter(AsnEncodingRules.DER); w.PushSequence();
        byte[] password = Encoding.Unicode.GetBytes(credentials.Password);
        try { Octets(w, 0, Encoding.Unicode.GetBytes(credentials.Domain)); Octets(w, 1, Encoding.Unicode.GetBytes(credentials.UserName)); Octets(w, 2, password); }
        finally { CryptographicOperations.ZeroMemory(password); }
        w.PopSequence(); byte[] inner = w.Encode();
        try { var outer = new AsnWriter(AsnEncodingRules.DER); outer.PushSequence(); Integer(outer, 0, 1); Octets(outer, 1, inner); outer.PopSequence(); return outer.Encode(); }
        finally { CryptographicOperations.ZeroMemory(inner); }
    }
    public static async ValueTask<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[5]; await stream.ReadExactlyAsync(header.AsMemory(0, 2), ct);
        if (header[0] != 0x30) throw new RdpProtocolException("Expected a CredSSP sequence.");
        int size = header[1], prefix = 2;
        if ((size & 128) != 0)
        {
            int count = size & 127;
            if (count is < 1 or > 3) throw new RdpProtocolException("Invalid CredSSP length form.");
            await stream.ReadExactlyAsync(header.AsMemory(2, count), ct); prefix += count; size = 0;
            for (int i = 0; i < count; i++) size = (size << 8) | header[2 + i];
        }
        if (size > 1_048_576 - prefix) throw new RdpProtocolException("CredSSP packet too large.");
        byte[] data = new byte[prefix + size]; header.AsSpan(0, prefix).CopyTo(data);
        await stream.ReadExactlyAsync(data.AsMemory(prefix), ct); return data;
    }
}
