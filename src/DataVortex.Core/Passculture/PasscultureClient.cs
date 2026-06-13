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
    private readonly ProxyPool _pool;
    private readonly TwoCaptchaService? _twoCaptcha;
    private readonly ILogger<PasscultureClient> _log;

    public PasscultureClient(ProxyPool pool, TwoCaptchaService? twoCaptcha = null, ILogger<PasscultureClient>? log = null)
    {
        _pool = pool;
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
            var http = _pool.Next();
            _log.LogInformation("→ Passculture POST signin pour {Email}", identifier);
            using var resp = await http.PostAsync("native/v1/signin", content, ct).ConfigureAwait(false);
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
                _log.LogInformation("← Passculture signin {Email}: HTTP {Code} success={Success} token={HasToken} state={State}",
                    identifier, (int)resp.StatusCode, resp.IsSuccessStatusCode && access is not null, access is not null, accountState);
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

    /// <summary>Solves ONE Passculture login captcha via 2captcha and returns the token (or null if no solver
    /// is configured / it failed). Callers solve once per account and reuse the token across 429 retries, so a
    /// rate-limited account never spends more than one captcha. When a non-null token is passed to
    /// <see cref="SignInAsync"/>, that method does not solve again.</summary>
    public async Task<string?> SolveCaptchaAsync(CancellationToken ct = default)
    {
        if (_twoCaptcha is null) return null;
        try
        {
            return await _twoCaptcha.SolveRecaptchaAsync(
                siteKey: "6LdWB0caAAAAAKfVe3he0FqXQXOepICF-5aZh_rQ",
                pageUrl: "https://passculture.app/connexion?preventCancellation=true",
                ct: ct).ConfigureAwait(false);
        }
        catch { return null; }
    }

    /// <summary>Mints a fresh access token from a (long-lived) refresh token — <b>no captcha required</b>.
    /// Used to re-read <c>/me</c> for accounts whose credit was never captured. Returns null on failure
    /// (e.g. the refresh token has expired, ~31 days).</summary>
    public async Task<string?> RefreshAccessTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "native/v1/refresh_access_token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshToken);
            var http = _pool.Next();
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.TryGetProperty("accessToken", out var a) ? a.GetString() : null;
        }
        catch { return null; }
    }

    public async Task<MeResult> GetMeAsync(string accessToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "native/v1/me");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var http = _pool.Next();
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
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
