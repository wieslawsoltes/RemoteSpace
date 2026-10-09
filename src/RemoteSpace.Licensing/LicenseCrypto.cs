using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RemoteSpace.Protocol;

namespace RemoteSpace.Licensing;

/// <summary>MS-RDPELE 5.1 algorithms, confined to licensing inside authenticated TLS. Not a general-purpose cryptography API.</summary>
public sealed class LicenseKeys : IDisposable
{
    private readonly byte[] _encryption;
    private readonly byte[] _mac;
    private bool _disposed;
    public LicenseKeys(ReadOnlySpan<byte> premaster, ReadOnlySpan<byte> clientRandom, ReadOnlySpan<byte> serverRandom)
    {
        if (premaster.Length != 48 || clientRandom.Length != 32 || serverRandom.Length != 32) throw new ArgumentException("Invalid licensing key material length.");
        byte[] master = Expand(premaster, clientRandom, serverRandom);
        byte[] session = Expand(master, serverRandom, clientRandom);
        try
        {
            _mac = session.AsSpan(0, 16).ToArray();
            _encryption = MD5.HashData(new PacketWriter().Bytes(session.AsSpan(16, 16)).Bytes(clientRandom).Bytes(serverRandom).ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(master); CryptographicOperations.ZeroMemory(session); }
    }
    private static byte[] Expand(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        byte[] expanded = new byte[48];
        for (int i = 0; i < 3; i++)
        {
            byte[] seed = new PacketWriter().Bytes(Encoding.ASCII.GetBytes(new string((char)('A' + i), i + 1))).Bytes(secret).Bytes(first).Bytes(second).ToArray();
            byte[] sha = SHA1.HashData(seed);
            byte[] input = new PacketWriter().Bytes(secret).Bytes(sha).ToArray();
            MD5.HashData(input).CopyTo(expanded, i * 16);
            CryptographicOperations.ZeroMemory(seed); CryptographicOperations.ZeroMemory(input); CryptographicOperations.ZeroMemory(sha);
        }
        return expanded;
    }
    public byte[] Mac(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] inner = new PacketWriter().Bytes(_mac).Bytes(Enumerable.Repeat((byte)0x36, 40).ToArray()).U32((uint)data.Length).Bytes(data).ToArray();
        byte[] hash = SHA1.HashData(inner);
        byte[] outer = new PacketWriter().Bytes(_mac).Bytes(Enumerable.Repeat((byte)0x5c, 48).ToArray()).Bytes(hash).ToArray();
        try { return MD5.HashData(outer); }
        finally { CryptographicOperations.ZeroMemory(inner); CryptographicOperations.ZeroMemory(outer); CryptographicOperations.ZeroMemory(hash); }
    }
    public byte[] Transform(ReadOnlySpan<byte> data) { ObjectDisposedException.ThrowIf(_disposed, this); return LicenseRc4.Transform(_encryption, data); }
    public byte[] DecryptVerified(ReadOnlySpan<byte> data, ReadOnlySpan<byte> mac)
    {
        byte[] plain = Transform(data);
        if (mac.Length != 16 || !CryptographicOperations.FixedTimeEquals(Mac(plain), mac))
        { CryptographicOperations.ZeroMemory(plain); throw new RdpProtocolException("The licensing message failed its integrity check."); }
        return plain;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; CryptographicOperations.ZeroMemory(_encryption); CryptographicOperations.ZeroMemory(_mac); }
}

/// <summary>The RC4 transform mandated by the legacy licensing extension. Never used for RDP transport security.</summary>
public static class LicenseRc4
{
    public static byte[] Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input)
    {
        if (key.Length is < 1 or > 256 || input.Length > 65535) throw new ArgumentOutOfRangeException(nameof(key));
        Span<byte> state = stackalloc byte[256]; for (int n = 0; n < 256; n++) state[n] = (byte)n;
        int j = 0;
        for (int n = 0; n < 256; n++) { j = (j + state[n] + key[n % key.Length]) & 255; (state[n], state[j]) = (state[j], state[n]); }
        byte[] result = new byte[input.Length]; int i = 0; j = 0;
        for (int n = 0; n < result.Length; n++)
        { i = (i + 1) & 255; j = (j + state[i]) & 255; (state[i], state[j]) = (state[j], state[i]); result[n] = (byte)(input[n] ^ state[(state[i] + state[j]) & 255]); }
        CryptographicOperations.ZeroMemory(state); return result;
    }
}

public sealed record LicensePublicKey(byte[] ModulusLittleEndian, uint Exponent)
{
    public byte[] EncryptPremaster(ReadOnlySpan<byte> premaster)
    {
        if (premaster.Length != 48 || ModulusLittleEndian.Length is < 64 or > 512 || Exponent < 3 || (Exponent & 1) == 0) throw new RdpProtocolException("Unsupported licensing RSA key.");
        var modulus = new BigInteger(ModulusLittleEndian, isUnsigned: true, isBigEndian: false);
        var plain = new BigInteger(premaster, isUnsigned: true, isBigEndian: false);
        if (modulus.IsEven || modulus.GetBitLength() < ModulusLittleEndian.Length * 8 - 1 || plain >= modulus) throw new RdpProtocolException("Invalid licensing RSA modulus.");
        byte[] encrypted = BigInteger.ModPow(plain, Exponent, modulus).ToByteArray(isUnsigned: true, isBigEndian: false);
        byte[] padded = new byte[ModulusLittleEndian.Length + 8]; encrypted.CopyTo(padded, 0); return padded;
    }
    // The certificate is received inside an already authenticated TLS channel. This parser does NOT establish server identity.
    public static LicensePublicKey Parse(ReadOnlySpan<byte> certificate)
    {
        var r = new PacketReader(certificate); uint version = r.U32() & 0x7fffffff;
        if (version == 1)
        {
            if (r.U32() != 1 || r.U32() != 1 || r.U16() != 6) throw new RdpProtocolException("Unsupported proprietary licensing certificate algorithm.");
            var key = new PacketReader(r.Bytes(r.U16()));
            if (key.U32() != 0x31415352) throw new RdpProtocolException("Invalid licensing RSA key magic.");
            uint keyLength = key.U32(), bits = key.U32(), dataLength = key.U32(), exponent = key.U32();
            if (keyLength is < 72 or > 520 || bits != (keyLength - 8) * 8 || dataLength != keyLength - 9) throw new RdpProtocolException("Inconsistent licensing RSA key lengths.");
            byte[] modulus = key.Bytes((int)keyLength - 8).ToArray(); key.Skip(8); key.End();
            if (r.U16() != 8) throw new RdpProtocolException("Invalid licensing signature blob type.");
            int signatureSize = r.U16(); if (signatureSize < 64) throw new RdpProtocolException("Truncated licensing certificate signature.");
            r.Skip(signatureSize); r.End(); return new(modulus, exponent);
        }
        if (version == 2)
        {
            uint count = r.U32(); if (count is < 1 or > 16) throw new RdpProtocolException("Invalid licensing certificate-chain count.");
            byte[] leaf = [];
            for (int i = 0; i < count; i++)
            { uint length = r.U32(); if (length is < 1 or > 65535) throw new RdpProtocolException("Invalid certificate size."); leaf = r.Bytes((int)length).ToArray(); }
            if (r.Remaining != 0 && r.Remaining != 8 + 4 * count) throw new RdpProtocolException("Invalid licensing certificate padding length.");
            r.Skip(r.Remaining);
            using var cert = X509CertificateLoader.LoadCertificate(leaf);
            using var rsa = cert.GetRSAPublicKey() ?? throw new RdpProtocolException("Licensing requires an RSA leaf certificate.");
            var parameters = rsa.ExportParameters(false); byte[] modulus = parameters.Modulus!; Array.Reverse(modulus);
            if (parameters.Exponent!.Length > 4) throw new RdpProtocolException("Unsupported licensing exponent.");
            uint exponent = 0; foreach (byte b in parameters.Exponent) exponent = (exponent << 8) | b;
            return new(modulus, exponent);
        }
        throw new RdpProtocolException("Unsupported licensing certificate version.");
    }
}
