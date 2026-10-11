using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteSpace.Security;

/// <summary>CA/name validation by default. An explicit SHA-256 pin identifies one certificate, never an arbitrary server.</summary>
public static class CertificatePolicy
{
    public static bool Validate(X509Certificate? certificate, SslPolicyErrors errors, string? sha256Pin, DateTimeOffset now)
    {
        if (certificate is null) return false;
        using var cert = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        if (now.UtcDateTime < cert.NotBefore.ToUniversalTime() || now.UtcDateTime > cert.NotAfter.ToUniversalTime()) return false;
        if (string.IsNullOrEmpty(sha256Pin)) return errors == SslPolicyErrors.None;
        if (sha256Pin.Length != 64 || !sha256Pin.All(Uri.IsHexDigit)) return false;
        return CryptographicOperations.FixedTimeEquals(cert.GetCertHash(HashAlgorithmName.SHA256), Convert.FromHexString(sha256Pin));
    }
}
