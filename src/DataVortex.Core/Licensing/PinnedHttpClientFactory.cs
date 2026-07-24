using System.Security.Cryptography;
using System.Text;

namespace DataVortex.Core.Licensing;

/// <summary>Builds the HttpClient the licence client talks to the server with. When a SPKI pin is configured, the
/// pin IS the trust anchor: a TLS connection is accepted only if the leaf certificate's public-key hash matches
/// the pin — regardless of CA chain trust or hostname. That is what lets us pin a <b>self-signed</b> server cert
/// (no public CA, connecting by bare IP), and it stays safe because only the holder of the matching PRIVATE key
/// can complete the TLS handshake — an attacker can't impersonate the server by replaying its public certificate.
/// An empty pin yields a standard client (normal TLS chain validation only), which is the dev default.</summary>
public static class PinnedHttpClientFactory
{
    public static HttpClient Create(string? spkiPinBase64)
    {
        if (string.IsNullOrEmpty(spkiPinBase64)) return new HttpClient();

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            {
                if (cert is null) return false;
                var hash = Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
                return CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(spkiPinBase64));
            }
        };
        return new HttpClient(handler);
    }
}
