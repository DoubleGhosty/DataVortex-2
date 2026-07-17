using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataVortex.Licensing;

namespace DataVortex.Core.Licensing;

/// <summary>HTTPS implementation of <see cref="ILicenseApiClient"/> against the licence server's REST API
/// (<c>/api/v1/activate|verify|renew|deactivate</c>). Transport failures throw and are turned into the grace /
/// "server unreachable" path by <see cref="LicenseManager"/>. Request signing (HMAC + nonce + timestamp) and
/// signed-response verification are layered on once the server side lands — the wire shape is fixed here.</summary>
public sealed class HttpLicenseApiClient : ILicenseApiClient
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly byte[] _hmacKey;

    public HttpLicenseApiClient(HttpClient http, string baseUrl, string? appHmacKey = null)
    {
        _http = http;
        _base = (baseUrl ?? "").TrimEnd('/');
        _hmacKey = string.IsNullOrEmpty(appHmacKey) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(appHmacKey);
    }

    public Task<LicenseResponse> ActivateAsync(ActivationRequest request, CancellationToken ct = default)
        => PostAsync("activate", new Dictionary<string, object?>
        {
            ["license_key"] = request.LicenseKey,
            ["fingerprint"] = FingerprintJson(request.Fingerprint),
            ["app_version"] = request.AppVersion,
        }, ct);

    public Task<LicenseResponse> VerifyAsync(string token, FingerprintSnapshot fingerprint, CancellationToken ct = default)
        => PostAsync("verify", new Dictionary<string, object?>
        {
            ["token"] = token,
            ["fingerprint"] = FingerprintJson(fingerprint),
        }, ct);

    public Task<LicenseResponse> RenewAsync(string token, CancellationToken ct = default)
        => PostAsync("renew", new Dictionary<string, object?> { ["token"] = token }, ct);

    public async Task DeactivateAsync(string token, CancellationToken ct = default)
        => await PostAsync("deactivate", new Dictionary<string, object?> { ["token"] = token }, ct).ConfigureAwait(false);

    private async Task<LicenseResponse> PostAsync(string endpoint, Dictionary<string, object?> body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/v1/{endpoint}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        SignRequest(req, json);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseResponse(resp.StatusCode, text);
    }

    /// <summary>Adds the app HMAC + timestamp + nonce headers so the server can authenticate the request and
    /// reject replays. No-op when no app key is embedded (dev).</summary>
    private void SignRequest(HttpRequestMessage req, string body)
    {
        if (_hmacKey.Length == 0) return;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var mac = Convert.ToBase64String(HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(ts + "." + nonce + "." + body)));
        req.Headers.TryAddWithoutValidation("X-Timestamp", ts);
        req.Headers.TryAddWithoutValidation("X-Nonce", nonce);
        req.Headers.TryAddWithoutValidation("X-Signature", mac);
    }

    private static object FingerprintJson(FingerprintSnapshot fp) => new Dictionary<string, object?>
    {
        ["components"] = fp.Components.Select(c => new Dictionary<string, object?>
        {
            ["id"] = c.Id, ["h"] = c.ValueHash, ["w"] = c.Weight
        }),
    };

    private static LicenseResponse ParseResponse(System.Net.HttpStatusCode code, string body)
    {
        if ((int)code == 429) return new(false, null, LicenseServerStatus.RateLimited, null);

        string? token = null, message = null, statusStr = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object)
            {
                if (r.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String) token = t.GetString();
                if (r.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
                if (r.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String) statusStr = s.GetString();
            }
        }
        catch { /* non-JSON body → fall back to the HTTP code */ }

        var status = MapStatus(statusStr, code, token);
        return new(status == LicenseServerStatus.Ok, token, status, message);
    }

    private static LicenseServerStatus MapStatus(string? status, System.Net.HttpStatusCode code, string? token)
    {
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<LicenseServerStatus>(status, ignoreCase: true, out var parsed))
            return parsed;

        return code switch
        {
            System.Net.HttpStatusCode.OK => token is not null ? LicenseServerStatus.Ok : LicenseServerStatus.ServerError,
            System.Net.HttpStatusCode.TooManyRequests => LicenseServerStatus.RateLimited,
            _ => LicenseServerStatus.ServerError,
        };
    }
}
