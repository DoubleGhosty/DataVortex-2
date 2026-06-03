using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataVortex.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Passculture;

public sealed class SignInResult
{
    public bool Success { get; init; }
    public string? AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public string? AccountState { get; init; }
    public string? StatusType { get; init; }
    public int StatusCode { get; init; }
    public string? Raw { get; init; }
}

public sealed class MeResult
{
    public bool Success { get; init; }
    public decimal? DomainsCreditRemaining { get; init; }
    public string? BirthDate { get; init; } // expected format: yyyy-MM-dd
    public string? Raw { get; init; }
}

public sealed class PasscultureClient
{
    private readonly HttpClient _http;
    private readonly TwoCaptchaService? _twoCaptcha;
    private readonly ILogger<PasscultureClient> _log;

    public PasscultureClient(HttpClient http, TwoCaptchaService? twoCaptcha = null, ILogger<PasscultureClient>? log = null)
    {
        _http = http;
        _twoCaptcha = twoCaptcha;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PasscultureClient>.Instance;
    }

    /// <summary>
    /// Attempts to sign in with identifier/username, password and optional captcha token.
    /// The caller must obtain captcha token (e.g. via 2captcha) and provide it here.
    /// </summary>
    public async Task<SignInResult> SignInAsync(string identifier, string password, string? captchaToken, CancellationToken ct = default)
    {
        var req = new Dictionary<string, object?>
        {
            ["identifier"] = identifier,
            ["password"] = password,
            ["token"] = captchaToken
        };

        var json = JsonSerializer.Serialize(req);
        HttpContent? content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            // If no captcha token provided and TwoCaptchaService is available, try to solve automatically
            if (string.IsNullOrWhiteSpace(captchaToken) && _twoCaptcha is not null)
            {
                try
                {
                    _log.LogInformation("Attempting to solve captcha via 2captcha");
                    var solved = await _twoCaptcha.SolveRecaptchaAsync(
                        siteKey: "6LdWB0caAAAAAKfVe3he0FqXQXOepICF-5aZh_rQ",
                        pageUrl: "https://passculture.app/connexion?preventCancellation=true");
                    _log.LogInformation("2captcha returned token: {TokenPresent}", !string.IsNullOrWhiteSpace(solved));
                    if (!string.IsNullOrWhiteSpace(solved))
                    {
                        // update body with solved token
                        req["token"] = solved;
                        json = JsonSerializer.Serialize(req);
                        try { content.Dispose(); } catch { }
                        content = new StringContent(json, Encoding.UTF8, "application/json");
                    }
                }
                catch { /* best-effort */ }
            }
            _log.LogDebug("Posting signin body: {Body}", json);
            using var resp = await _http.PostAsync("native/v1/signin", content, ct).ConfigureAwait(false);
            var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _log.LogDebug("Signin response: {Response}", s);
            try
            {
                using var doc = JsonDocument.Parse(s);
                var root = doc.RootElement;
                string? access = null;
                string? accountState = null;
                string? statusType = null;
                string? refresh = null;
                if (root.TryGetProperty("accessToken", out var a)) access = a.GetString();
                if (root.TryGetProperty("refreshToken", out var rt)) refresh = rt.GetString();
                if (root.TryGetProperty("accountState", out var asv)) accountState = asv.GetString();
                if (root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object)
                {
                    if (st.TryGetProperty("statusType", out var stt)) statusType = stt.GetString();
                }
                _log.LogInformation("Signin success={Success} accessTokenPresent={HasToken} accountState={State}", resp.IsSuccessStatusCode, access is not null, accountState);
                return new SignInResult { Success = resp.IsSuccessStatusCode && access is not null, AccessToken = access, RefreshToken = refresh, AccountState = accountState, StatusType = statusType, StatusCode = (int)resp.StatusCode, Raw = s };
            }
            catch
            {
                return new SignInResult { Success = resp.IsSuccessStatusCode, StatusCode = (int)resp.StatusCode, Raw = s };
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Signin exception");
            return new SignInResult { Success = false, Raw = ex.Message };
        }
    }

    public async Task<MeResult> GetMeAsync(string accessToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "native/v1/me");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(s);
                var root = doc.RootElement;
                decimal? credit = null;
                string? birth = null;
                if (root.TryGetProperty("domainsCredit", out var dc) && dc.TryGetProperty("all", out var all) && all.TryGetProperty("remaining", out var rem))
                {
                    if (rem.TryGetDecimal(out var d)) credit = d;
                }
                if (root.TryGetProperty("birthDate", out var bd)) birth = bd.GetString();
                return new MeResult { Success = resp.IsSuccessStatusCode, DomainsCreditRemaining = credit, BirthDate = birth, Raw = s };
            }
            catch
            {
                return new MeResult { Success = resp.IsSuccessStatusCode, Raw = s };
            }
        }
        catch (Exception ex)
        {
            return new MeResult { Success = false, Raw = ex.Message };
        }
    }
}
