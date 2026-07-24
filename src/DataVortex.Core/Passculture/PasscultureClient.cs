using System.Text;
using System.Text.Json;
using DataVortex.Core.Licensing;
using DataVortex.Core.Models;
using DataVortex.Licensing;
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
    private readonly ILicenseGate? _gate;
    private readonly IRecipeSource? _recipe;

    public PasscultureClient(ProxyPool pool, ICaptchaSolver? captcha = null, ILogger<PasscultureClient>? log = null,
        ILicenseGate? gate = null, IRecipeSource? recipe = null)
    {
        _pool = pool;
        _captcha = captcha;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PasscultureClient>.Instance;
        _gate = gate;
        _recipe = recipe;
    }

    /// <summary>The live operational recipe (Palier C): the backend URL, reCAPTCHA site-key, page URL and endpoint
    /// paths — delivered by the server per session, held in memory only. Null when there is no session, which fails
    /// every checker call closed (there is nothing hard-coded to fall back on).</summary>
    private OperationalRecipe? Recipe => _recipe?.Current;

    /// <summary>Absolute request uri = recipe base URL + relative endpoint path (no backend URL is embedded).</summary>
    private static Uri Endpoint(OperationalRecipe r, string path) => new(new Uri(r.BaseUrl), path);

    /// <summary>How long a proxy is benched after the backend rate-limits it (HTTP 429).</summary>
    private static readonly TimeSpan RateLimitBan = TimeSpan.FromMinutes(5);

    /// <summary>How long a proxy is benched after a transient transport failure (tunnel 502/503/504, reset,
    /// timeout) — shorter than the rate-limit ban since these are usually fleeting.</summary>
    private static readonly TimeSpan ProxyFailBan = TimeSpan.FromMinutes(2);

    /// <summary>Extra send attempts on a transient proxy/rate-limit failure (so up to 3 sends total). Each retry
    /// rotates to the next pooled proxy — with a rotating gateway that means a fresh exit IP.</summary>
    private const int MaxTransportRetries = 2;

    /// <summary>Adds the Bearer token when present. The backend authenticates on this alone — the extra
    /// "web-app" headers we briefly sent (app-version/platform/device-id/…) turned out not to matter.</summary>
    private static void ApplyAuth(HttpRequestMessage req, string? bearer)
    {
        if (!string.IsNullOrEmpty(bearer))
            req.Headers.TryAddWithoutValidation("authorization", "Bearer " + bearer);
    }

    /// <summary>Sends a request through the next pooled proxy, retrying on a rate-limit (HTTP 429) or a transient
    /// proxy/transport failure (502/503/504, or a tunnel/connection exception) by benching the failing proxy and
    /// rotating to the next one — with a rotating gateway this re-exits through a different IP. The request is
    /// rebuilt each attempt via <paramref name="reqFactory"/> (an HttpRequestMessage is single-use). A definitive
    /// HTTP response (200/400/401/…) is returned immediately, never retried. On exhausted transport retries the
    /// last exception is rethrown. The caller owns disposal of the returned response.</summary>
    private async Task<HttpResponseMessage> SendThroughPoolAsync(Func<HttpRequestMessage> reqFactory, string path, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var http = _pool.Next();
            try
            {
                using var req = reqFactory();
                var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

                int code = (int)resp.StatusCode;
                if (code is 429 or 502 or 503 or 504)
                {
                    _pool.Ban(http, code == 429 ? RateLimitBan : ProxyFailBan);
                    if (attempt < MaxTransportRetries)
                    {
                        _log.LogDebug("{Path}: HTTP {Code} → proxy en quarantaine, rotation (tentative {Next}, {Avail} dispo)",
                            path, code, attempt + 2, _pool.AvailableCount);
                        resp.Dispose();
                        continue;
                    }
                    _log.LogWarning("{Path}: HTTP {Code} persistant après {Total} tentative(s) (rate limit / proxy)",
                        path, code, attempt + 1);
                }
                return resp;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // genuine cancellation (Stop) — not a transport failure
            }
            catch (Exception ex)
            {
                // Transient transport/proxy failure (Bright Data tunnel 502, connection reset, timeout): no HTTP
                // response. Bench this proxy and rotate; log the message only — the stack trace is always the same
                // HttpConnectionPool plumbing and just floods the log.
                _pool.Ban(http, ProxyFailBan);
                if (attempt < MaxTransportRetries)
                {
                    _log.LogDebug("{Path}: échec proxy ({Error}) → rotation (tentative {Next}, {Avail} dispo)",
                        path, ex.Message, attempt + 2, _pool.AvailableCount);
                    continue;
                }
                throw;
            }
        }
    }

    /// <summary>
    /// Attempts to sign in with identifier/username, password and optional captcha token.
    /// The caller must obtain captcha token (e.g. via 2captcha) and provide it here.
    /// </summary>
    public async Task<SignInResult> SignInAsync(string identifier, string password, string? captchaToken, CancellationToken ct = default)
    {
        // Capability gate at the jewel: no CheckPassculture entitlement ⇒ no sign-in ⇒ the checker produces nothing.
        _gate?.Require(Capability.CheckPassculture);

        // Palier C: the recipe (site-key, page URL, endpoints, base URL) comes from the live session ONLY. No
        // session ⇒ no recipe ⇒ the checker cannot even build the request — nothing hard-coded to patch.
        var recipe = Recipe;
        if (recipe is null || !recipe.IsComplete)
        {
            _log.LogWarning("Sign-in blocked: no operational session (recipe unavailable)");
            return new SignInResult { Success = false, Raw = "no operational session" };
        }

        var req = new Dictionary<string, object?>
        {
            ["identifier"] = identifier,
            ["password"] = password,
            ["token"] = captchaToken
        };

        var json = JsonSerializer.Serialize(req);
        try
        {
            // If no captcha token provided and TwoCaptchaService is available, try to solve automatically
            if (string.IsNullOrWhiteSpace(captchaToken) && _captcha is not null)
            {
                try
                {
                    _log.LogInformation("Attempting to solve captcha via 2captcha");
                    var solved = await _captcha.SolveRecaptchaAsync(siteKey: recipe.SiteKey, pageUrl: recipe.PageUrl);
                    _log.LogInformation("2captcha returned token: {TokenPresent}", !string.IsNullOrWhiteSpace(solved));
                    if (!string.IsNullOrWhiteSpace(solved))
                    {
                        req["token"] = solved; // update body with the solved token
                        json = JsonSerializer.Serialize(req);
                    }
                }
                catch { /* best-effort */ }
            }
            _log.LogInformation("→ Passculture POST signin pour {Email}", identifier);
            // Rebuilt each attempt (the content stream is single-use); a 502 on one proxy retries on the next.
            using var resp = await SendThroughPoolAsync(
                () => new HttpRequestMessage(HttpMethod.Post, Endpoint(recipe, recipe.SignInPath))
                      { Content = new StringContent(json, Encoding.UTF8, "application/json") },
                "signin", ct).ConfigureAwait(false);
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
            // Network/proxy/timeout failure on the signin POST (NOT a backend rejection). Surface the reason
            // (message only — the stack trace is just HttpConnectionPool plumbing).
            _log.LogWarning("Signin exception pour {Email}: {Error}", identifier, ex.Message);
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
        var recipe = Recipe;
        if (recipe is null || !recipe.IsComplete) return null;
        try
        {
            return await _captcha.SolveRecaptchaAsync(siteKey: recipe.SiteKey, pageUrl: recipe.PageUrl, ct: ct).ConfigureAwait(false);
        }
        catch { return null; }
    }

    /// <summary>Mints a fresh access token from a (long-lived) refresh token — <b>no captcha required</b>.
    /// Used to re-read <c>/me</c> for accounts whose credit was never captured. Returns null on failure
    /// (e.g. the refresh token has expired, ~31 days).</summary>
    public async Task<RefreshResult> RefreshAccessTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var recipe = Recipe;
        if (recipe is null || !recipe.IsComplete) return new RefreshResult { StatusCode = 0 };
        try
        {
            using var resp = await SendThroughPoolAsync(
                () => { var r = new HttpRequestMessage(HttpMethod.Post, Endpoint(recipe, recipe.RefreshPath)); ApplyAuth(r, refreshToken); return r; },
                "refresh", ct).ConfigureAwait(false);
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
        catch (Exception ex) { _log.LogDebug("Refresh exception: {Error}", ex.Message); return new RefreshResult { StatusCode = 0 }; }
    }

    /// <summary>Reactivates a user-suspended account: <c>POST native/v1/account/unsuspend</c> with the user's
    /// bearer token (empty body). Returns the HTTP status — <b>204</b> on success. The backend enforces the real
    /// conditions (feature flag ENABLE_UNSUSPEND_ACCOUNT, suspension reason UPON_USER_REQUEST, within the
    /// ACCOUNT_UNSUSPENSION_DELAY) and answers 403 otherwise. Returns 0 on a network/proxy failure.</summary>
    public async Task<int> UnsuspendAsync(string accessToken, CancellationToken ct = default)
    {
        var recipe = Recipe;
        if (recipe is null || !recipe.IsComplete) return 0;
        try
        {
            using var resp = await SendThroughPoolAsync(
                () => { var r = new HttpRequestMessage(HttpMethod.Post, Endpoint(recipe, recipe.UnsuspendPath)); ApplyAuth(r, accessToken); return r; },
                "unsuspend", ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _log.LogInformation("Unsuspend → HTTP {Code}: {Body}", (int)resp.StatusCode, body);
            return (int)resp.StatusCode;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Unsuspend exception: {Error}", ex.Message);
            return 0;
        }
    }

    public async Task<MeResult> GetMeAsync(string accessToken, CancellationToken ct = default)
    {
        var recipe = Recipe;
        if (recipe is null || !recipe.IsComplete) return new MeResult { Success = false };
        try
        {
            using var resp = await SendThroughPoolAsync(
                () => { var r = new HttpRequestMessage(HttpMethod.Get, Endpoint(recipe, recipe.MePath)); ApplyAuth(r, accessToken); return r; },
                "me", ct).ConfigureAwait(false);
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
            _log.LogWarning("GetMe exception: {Error}", ex.Message);
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
