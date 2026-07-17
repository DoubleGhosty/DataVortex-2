using System.Net.Security;
using System.Security.Cryptography;
using System.Text;

namespace DataVortex.Core.Licensing;

/// <summary>Builds the HttpClient the licence client talks to the server with. When a SPKI pin is configured, a
/// TLS connection whose leaf certificate's public-key hash doesn't match the pin is rejected (certificate
/// pinning) — so a rogue-CA MITM can't impersonate the server. An empty pin yields a standard client (normal
/// TLS chain validation only), which is the dev default.</summary>
public static class PinnedHttpClientFactory
{
    public static HttpClient Create(string? spkiPinBase64)
    {
        if (string.IsNullOrEmpty(spkiPinBase64)) return new HttpClient();

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (cert is null || errors != SslPolicyErrors.None) return false; // normal chain must still pass
                var hash = Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
                return CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(spkiPinBase64));
            }
        };
        return new HttpClient(handler);
    }
}
