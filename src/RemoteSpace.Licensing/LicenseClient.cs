using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RemoteSpace.Protocol;

namespace RemoteSpace.Licensing;

public sealed record LicenseProduct(uint Version, string Company, string ProductId);
public sealed record IssuedLicense(LicenseProduct Product, string Scope, byte[] Certificate);
public sealed record LicenseResponse(byte[]? Reply, bool Completed, IssuedLicense? Issued = null);

/// <summary>Original MS-RDPELE new-license/challenge/issuance state machine. The server remains the licensing authority.</summary>
public sealed class LicenseClient : IDisposable
{
    private const uint PlatformId = 0x04000000;
    private readonly string _user;
    private readonly string _machine;
    private readonly Func<LicenseProduct, IReadOnlyList<string>, IssuedLicense?>? _lookup;
    private readonly byte[] _hardware;
    private LicenseKeys? _keys;
    private byte _version = 3;
    private int _rounds;
    private bool _requested, _challenged, _completed;
    private LicenseProduct? _product;
    public bool IsCompleted => _completed;

    public LicenseClient(string user, string machine, bool authenticatedTransport, Func<LicenseProduct, IReadOnlyList<string>, IssuedLicense?>? lookup = null)
    {
        if (!authenticatedTransport) throw new ArgumentException("Licensing must run inside an authenticated, encrypted transport.");
        _user = user; _machine = machine; _lookup = lookup;
        _hardware = new PacketWriter().U32(PlatformId).Bytes(MD5.HashData(Encoding.UTF8.GetBytes(machine))).ToArray();
    }
    public static bool IsLicensePacket(ReadOnlySpan<byte> bytes) => bytes.Length >= 4 && (BinaryPrimitives.ReadUInt16LittleEndian(bytes) & 0x80) != 0;
    public static byte[] Envelope(byte type, byte version, ReadOnlySpan<byte> body) => new PacketWriter().U16(0x80).U16(0).U8(type).U8(version).U16(body.Length + 4).Bytes(body).ToArray();
    private static ReadOnlySpan<byte> Blob(ref PacketReader r, ushort? expected = null)
    {
        ushort type = r.U16(); int length = r.U16();
        if (expected is { } required && type != required) throw new RdpProtocolException($"Unexpected licensing blob type 0x{type:X4}.");
        return r.Bytes(length);
    }
    private static PacketWriter Blob(PacketWriter w, ushort type, ReadOnlySpan<byte> data) => w.U16(type).U16(data.Length).Bytes(data);
    private static string Utf16(ref PacketReader r)
    {
        uint length = r.U32(); if (length is < 2 or > 4096 || (length & 1) != 0) throw new RdpProtocolException("Invalid licensing Unicode string length.");
        var bytes = r.Bytes((int)length);
        if (bytes[^1] != 0 || bytes[^2] != 0) throw new RdpProtocolException("Licensing Unicode string is not terminated.");
        return new UnicodeEncoding(false, false, true).GetString(bytes[..^2]);
    }
    private static byte[] Ansi(string text)
    {
        if (text.Length > 255 || text.Any(c => c == 0 || c > 127)) throw new NotSupportedException("This legacy licensing path requires an ASCII client/user name; no lossy name conversion is performed.");
        return Encoding.ASCII.GetBytes(text + "\0");
    }
    public LicenseResponse Accept(ReadOnlySpan<byte> packet)
    {
        if (_completed || ++_rounds > 8) throw new RdpProtocolException("Unexpected licensing message after completion or round limit exceeded.");
        var r = new PacketReader(packet); ushort flags = r.U16(); r.U16(); // flagsHi is reserved and MUST be ignored (MS-RDPBCGR 2.2.8.1.1.2.1).
        if ((flags & 0x80) == 0 || (flags & ~0x82b0) != 0) throw new RdpProtocolException("Unsupported licensing security flags.");
        byte type = r.U8(), version = r.U8();
        if ((version & 15) is not (2 or 3) || (version & 0x70) != 0 || r.U16() != packet.Length - 4) throw new RdpProtocolException("Invalid licensing preamble.");
        _version = (byte)Math.Min(3, version & 15);
        switch (type)
        {
            case 1:
                if (_requested) throw new RdpProtocolException("Repeated license request.");
                _requested = true; return new(Request(ref r), false);
            case 2:
                if (!_requested || _challenged || _keys is null) throw new RdpProtocolException("Unexpected licensing challenge.");
                _challenged = true; return new(Challenge(ref r), false);
            case 3: case 4:
                if (!_requested || _keys is null) throw new RdpProtocolException("Unsolicited license issuance.");
                var encrypted = Blob(ref r); var mac = r.Bytes(16); r.End();
                byte[] plain = _keys.DecryptVerified(encrypted, mac);
                try
                {
                    var issued = new PacketReader(plain); uint productVersion = issued.U32();
                    uint scopeLength = issued.U32(); if (scopeLength is < 1 or > 4096) throw new RdpProtocolException("Invalid issued license scope.");
                    var scopeBytes = issued.Bytes((int)scopeLength); if (scopeBytes[^1] != 0) throw new RdpProtocolException("Unterminated license scope.");
                    string scope = Encoding.Latin1.GetString(scopeBytes[..^1]); string company = Utf16(ref issued), product = Utf16(ref issued);
                    uint size = issued.U32(); if (size is < 1 or > 60000) throw new RdpProtocolException("Invalid issued CAL length.");
                    byte[] cal = issued.Bytes((int)size).ToArray(); issued.End();
                    var identity = new LicenseProduct(productVersion, company, product);
                    if (identity != _product) throw new RdpProtocolException("Issued license product does not match the server request.");
                    _completed = true; return new(null, true, new(identity, scope, cal));
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            case 0xff:
                uint error = r.U32(), transition = r.U32(); Blob(ref r); r.End();
                if (error != 7 || transition != 2) throw new RdpProtocolException($"Licensing server rejected the client: status 0x{error:X8}, transition {transition}. No license bypass is attempted.");
                _completed = true; return new(null, true);
            default: throw new RdpProtocolException($"Unsupported licensing message 0x{type:X2}.");
        }
    }
    private byte[] Request(ref PacketReader r)
    {
        byte[] server = r.Bytes(32).ToArray(); uint productVersion = r.U32();
        _product = new(productVersion, Utf16(ref r), Utf16(ref r));
        var algorithms = new PacketReader(Blob(ref r, 13)); bool rsa = false;
        while (algorithms.Remaining > 0) rsa |= algorithms.U32() == 1;
        if (!rsa) throw new RdpProtocolException("The licensing server did not advertise RSA key exchange.");
        var certificate = Blob(ref r, 3);
        if (certificate.Length == 0) throw new NotSupportedException("The server omitted its licensing certificate; legacy MCS certificate fallback is not available on this TLS-only transport.");
        var key = LicensePublicKey.Parse(certificate); uint scopesCount = r.U32();
        if (scopesCount > 64) throw new RdpProtocolException("Too many licensing scopes.");
        var scopes = new List<string>();
        for (int i = 0; i < scopesCount; i++)
        {
            var scope = Blob(ref r, 14); if (scope.Length is < 1 or > 4096 || scope[^1] != 0) throw new RdpProtocolException("Invalid licensing scope.");
            scopes.Add(Encoding.Latin1.GetString(scope[..^1]));
        }
        r.End();
        byte[] random = RandomNumberGenerator.GetBytes(32), premaster = RandomNumberGenerator.GetBytes(48);
        try
        {
            _keys = new(premaster, random, server);
            var body = new PacketWriter().U32(1).U32(PlatformId).Bytes(random);
            Blob(body, 2, key.EncryptPremaster(premaster));
            IssuedLicense? cached = _lookup?.Invoke(_product, scopes);
            if (cached is not null && cached.Product == _product && scopes.Contains(cached.Scope) && cached.Certificate.Length is > 0 and < 60000)
            {
                Blob(body, 1, cached.Certificate); Blob(body, 9, _keys.Transform(_hardware)); body.Bytes(_keys.Mac(_hardware));
                return Envelope(0x12, (byte)(_version | 0x80), body.ToArray());
            }
            Blob(body, 15, Ansi(_user)); Blob(body, 16, Ansi(_machine));
            return Envelope(0x13, (byte)(_version | 0x80), body.ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(premaster); }
    }
    private byte[] Challenge(ref PacketReader r)
    {
        r.U32(); var encrypted = Blob(ref r); var mac = r.Bytes(16); r.End();
        byte[] challenge = _keys!.DecryptVerified(encrypted, mac);
        if (challenge.Length > 4096) throw new RdpProtocolException("Licensing challenge exceeds the size limit.");
        try
        {
            byte[] response = new PacketWriter().U16(0x100).U16(0xff00).U16(3).U16(challenge.Length).Bytes(challenge).ToArray();
            var w = new PacketWriter(); Blob(w, 9, _keys.Transform(response)); Blob(w, 9, _keys.Transform(_hardware));
            w.Bytes(_keys.Mac(new PacketWriter().Bytes(response).Bytes(_hardware).ToArray()));
            return Envelope(0x15, (byte)(_version | 0x80), w.ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(challenge); }
    }
    public void Dispose() { _keys?.Dispose(); _completed = true; CryptographicOperations.ZeroMemory(_hardware); }
}