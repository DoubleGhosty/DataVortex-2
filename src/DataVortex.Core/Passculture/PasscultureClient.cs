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
    public int StatusCode { get; init; }
    public string? Raw { get; init; }
}

public sealed class MeResult
{
    public bool Success { get; init; }
    public int StatusCode { get; init; }
    public decimal? DomainsCreditRemaining { get; init; }
    public string? BirthDate { get; init; } // expected format: yyyy-MM-dd
    public string? StatusType { get; init; } // status.statusType e.g. "non_eligible", "ex_beneficiary"
    public DateTime? EligibilityEnd { get; init; } // eligibilityEndDatetime (UTC); non_eligible past this = expired opportunity
}

/// <summary>Outcome of a refresh-token call: the new access token (when granted) and the HTTP status, so the
/// caller can tell "session still valid" (200) from "token revoked / account restricted" (401/403).</summary>
public sealed class RefreshResult
{
    public string? AccessToken { get; init; }
    public string? RefreshToken { get; init; } // a new refresh token if the backend rotates it
    public int StatusCode { get; init; }
}

public sealed class PasscultureClient
{
    private readonly ProxyPool _pool;
    private readonly ICaptchaSolver? _captcha;
    private readonly ILogger<PasscultureClient> _log;

    public PasscultureClient(ProxyPool pool, ICaptchaSolver? captcha = null, ILogger<PasscultureClient>? log = null)
    {
        _pool = pool;
        _captcha = captcha;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PasscultureClient>.Instance;
    }

    // The passculture backend rejects API calls (notably /me → 401) that don't carry the web-app headers a real
    // browser sends — the app-version/platform/device-id/user-agent are validated server-side. We replicate the
    // headers captured from a working passculture.app /me request so our requests are accepted like the browser's.
    private const string AppVersion = "1.394.0";   // from passculture.app; bump if the API starts rejecting it
    private const string CommitHash = "ab3ce90";
    private const string WebUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36";
    private static readonly string DeviceId = Guid.NewGuid().ToString(); // one stable "device" for the whole run

    /// <summary>Adds the passculture web-app headers (and the Bearer token, if any) so a request is accepted like
    /// the real browser's — without these, <c>/me</c> answers 401 even with a valid token.</summary>
    private static void ApplyHeaders(HttpRequestMessage req, string? bearer)
    {
        if (!string.IsNullOrEmpty(bearer)) req.Headers.TryAddWithoutValidation("authorization", "Bearer " + bearer);
        req.Headers.TryAddWithoutValidation("accept", "*/*");
        req.Headers.TryAddWithoutValidation("accept-language", "en-US,en;q=0.9");
        req.Headers.TryAddWithoutValidation("app-version", AppVersion);
        req.Headers.TryAddWithoutValidation("commit-hash", CommitHash);
        req.Headers.TryAddWithoutValidation("device-id", DeviceId);
        req.Headers.TryAddWithoutValidation("origin", "https://passculture.app");
        req.Headers.TryAddWithoutValidation("platform", "web");
        req.Headers.TryAddWithoutValidation("referer", "https://passculture.app/");
        req.Headers.TryAddWithoutValidation("request-id", Guid.NewGuid().ToString());
        req.Headers.TryAddWithoutValidation("user-agent", WebUserAgent);
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
            if (string.IsNullOrWhiteSpace(captchaToken) && _captcha is not null)
            {
                try
                {
                    _log.LogInformation("Attempting to solve captcha via 2captcha");
                    var solved = await _captcha.SolveRecaptchaAsync(
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
            using var sreq = new HttpRequestMessage(HttpMethod.Post, "native/v1/signin") { Content = content };
            ApplyHeaders(sreq, null);
            using var resp = await http.SendAsync(sreq, ct).ConfigureAwait(false);
            var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Debug aid: show the raw login response body (HTTP code + email) in the live log.
            _log.LogInformation("Signin response [{Email}] HTTP {Code}: {Response}", identifier, (int)resp.StatusCode, s);
            try
            {
                using var doc = JsonDocument.Parse(s);
                var root = doc.RootElement;
                string? access = null;
                string? accountState = null;
                string? refresh = null;
                if (root.TryGetProperty("accessToken", out var a)) access = a.GetString();
                if (root.TryGetProperty("refreshToken", out var rt)) refresh = rt.GetString();
                if (root.TryGetProperty("accountState", out var asv)) accountState = asv.GetString();
                _log.LogInformation("← Passculture signin {Email}: HTTP {Code} success={Success} token={HasToken} state={State}",
                    identifier, (int)resp.StatusCode, resp.IsSuccessStatusCode && access is not null, access is not null, accountState);
                return new SignInResult { Success = resp.IsSuccessStatusCode && access is not null, AccessToken = access, RefreshToken = refresh, AccountState = accountState, StatusCode = (int)resp.StatusCode, Raw = s };
            }
            catch
            {
                return new SignInResult { Success = resp.IsSuccessStatusCode, StatusCode = (int)resp.StatusCode, Raw = s };
            }
        }
        catch (Exception ex)
        {
            // Network/proxy/timeout failure on the signin POST (NOT a backend rejection). Surface the reason.
            _log.LogWarning(ex, "Signin exception pour {Email}: {Error}", identifier, ex.Message);
            return new SignInResult { Success = false, Raw = ex.Message };
        }
    }

    /// <summary>Solves ONE Passculture login captcha via 2captcha and returns the token (or null if no solver
    /// is configured / it failed). Callers solve once per account and reuse the token across 429 retries, so a
    /// rate-limited account never spends more than one captcha. When a non-null token is passed to
    /// <see cref="SignInAsync"/>, that method does not solve again.</summary>
    public async Task<string?> SolveCaptchaAsync(CancellationToken ct = default)
    {
        if (_captcha is null) return null;
        try
        {
            return await _captcha.SolveRecaptchaAsync(
                siteKey: "6LdWB0caAAAAAKfVe3he0FqXQXOepICF-5aZh_rQ",
                pageUrl: "https://passculture.app/connexion?preventCancellation=true",
                ct: ct).ConfigureAwait(false);
        }
        catch { return null; }
    }

    /// <summary>Mints a fresh access token from a (long-lived) refresh token — <b>no captcha required</b>.
    /// Used to re-read <c>/me</c> for accounts whose credit was never captured. Returns null on failure
    /// (e.g. the refresh token has expired, ~31 days).</summary>
    public async Task<RefreshResult> RefreshAccessTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "native/v1/refresh_access_token");
            ApplyHeaders(req, refreshToken);
            var http = _pool.Next();
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string? token = null, newRefresh = null;
            if (resp.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(s);
                    if (doc.RootElement.TryGetProperty("accessToken", out var a)) token = a.GetString();
                    if (doc.RootElement.TryGetProperty("refreshToken", out var r)) newRefresh = r.GetString();
                }
                catch { /* unparseable body */ }
            }
            _log.LogDebug("Refresh [{Code}] accessToken={HasToken} (len={Len}) rotated={Rotated}: {Body}",
                (int)resp.StatusCode, !string.IsNullOrEmpty(token), token?.Length ?? 0,
                !string.IsNullOrEmpty(newRefresh), Truncate(s, 160));
            return new RefreshResult { AccessToken = token, RefreshToken = newRefresh, StatusCode = (int)resp.StatusCode };
        }
        catch (Exception ex) { _log.LogDebug(ex, "Refresh exception: {Error}", ex.Message); return new RefreshResult { StatusCode = 0 }; }
    }

    /// <summary>Reactivates a user-suspended account: <c>POST native/v1/account/unsuspend</c> with the user's
    /// bearer token (empty body). Returns the HTTP status — <b>204</b> on success. The backend enforces the real
    /// conditions (feature flag ENABLE_UNSUSPEND_ACCOUNT, suspension reason UPON_USER_REQUEST, within the
    /// ACCOUNT_UNSUSPENSION_DELAY) and answers 403 otherwise. Returns 0 on a network/proxy failure.</summary>
    public async Task<int> UnsuspendAsync(string accessToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "native/v1/account/unsuspend");
            ApplyHeaders(req, accessToken);
            var http = _pool.Next();
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _log.LogInformation("Unsuspend → HTTP {Code}: {Body}", (int)resp.StatusCode, body);
            return (int)resp.StatusCode;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Unsuspend exception: {Error}", ex.Message);
            return 0;
        }
    }

    public async Task<MeResult> GetMeAsync(string accessToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "native/v1/me");
            ApplyHeaders(req, accessToken);
            var http = _pool.Next();
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var me = ParseMe(s, (int)resp.StatusCode, resp.IsSuccessStatusCode);
            if (!me.Success)
                _log.LogWarning("GetMe [{Code}] réponse inattendue (pas un /me) [tok.len={Len}]: {Body}",
                    (int)resp.StatusCode, accessToken?.Length ?? 0, Truncate(s, 300));
            else
                _log.LogDebug("GetMe [{Code}] ok: statusType={Status} crédit={Credit}", me.StatusCode, me.StatusType, me.DomainsCreditRemaining);
            return me;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GetMe exception: {Error}", ex.Message);
            return new MeResult { Success = false };
        }
    }

    /// <summary>Parses a <c>/me</c> response body into a <see cref="MeResult"/>. A usable read requires an HTTP
    /// success AND a JSON object carrying the account identity (<c>id</c>/<c>email</c>); a non-200, an unparseable
    /// body, or a 200 that is not a /me (proxy interstitial / error JSON) yields <c>Success=false</c>. Every nested
    /// field is read defensively — <c>domainsCredit</c> is JSON <c>null</c> for non-beneficiaries, and without the
    /// ValueKind guards the nested lookup would throw and drop the status (misclassifying the account as VALIDE).</summary>
    public static MeResult ParseMe(string? body, int statusCode, bool isSuccess)
    {
        JsonElement root = default;
        bool parsed = false;
        try { using var doc = JsonDocument.Parse(body ?? ""); root = doc.RootElement.Clone(); parsed = true; }
        catch { /* body is not JSON */ }

        bool looksLikeMe = parsed && root.ValueKind == JsonValueKind.Object
            && (root.TryGetProperty("id", out _) || root.TryGetProperty("email", out _));
        if (!isSuccess || !looksLikeMe)
            return new MeResult { Success = false, StatusCode = statusCode };

        decimal? credit = null;
        if (root.TryGetProperty("domainsCredit", out var dc) && dc.ValueKind == JsonValueKind.Object
            && dc.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.Object
            && all.TryGetProperty("remaining", out var rem) && rem.TryGetDecimal(out var d))
            credit = d;

        string? birth = root.TryGetProperty("birthDate", out var bd) ? bd.GetString() : null;

        string? statusType = null;
        if (root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object
            && st.TryGetProperty("statusType", out var stt))
            statusType = stt.GetString();

        DateTime? eligEnd = null;
        if (root.TryGetProperty("eligibilityEndDatetime", out var ee) && ee.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(ee.GetString(), out var eeo))
            eligEnd = eeo.UtcDateTime;

        return new MeResult { Success = true, StatusCode = statusCode, DomainsCreditRemaining = credit, BirthDate = birth, StatusType = statusType, EligibilityEnd = eligEnd };
    }

    /// <summary>Shortens a response body for logging (keeps the live log readable).</summary>
    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
}
