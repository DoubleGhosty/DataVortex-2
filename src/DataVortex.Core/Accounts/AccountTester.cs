using DataVortex.Core.Models;
using DataVortex.Core.Passculture;

namespace DataVortex.Core.Accounts;

/// <summary>
/// The single, shared path for testing one credential against Passculture. Every caller (live processing,
/// the startup catch-up scan, and the manual "Test accounts" button) goes through here, so an account is
/// reserved atomically in <see cref="IAccountTestRegistry"/> and sent to the backend at most once — no
/// matter how many records or files contain it, across workers and across restarts.
/// </summary>
public static class AccountTester
{
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

        try
        {
            var signin = await passClient.SignInAsync(cred.Username ?? "", cred.Password ?? "", null, ct).ConfigureAwait(false);

            // StatusCode 0 == no HTTP response (network error): release so it can be retried later.
            if (signin.StatusCode == 0)
            {
                registry.Release(cred.Username, cred.Password);
                return cred;
            }

            decimal? credit = null;
            string? birth = null;
            if (signin.Success && signin.AccessToken is not null)
            {
                try
                {
                    var me = await passClient.GetMeAsync(signin.AccessToken, ct).ConfigureAwait(false);
                    credit = me.DomainsCreditRemaining;
                    birth = me.BirthDate;
                }
                catch { /* credit/birth are best-effort */ }
            }

            var result = new AccountTestResult(
                signin.Success, signin.StatusCode, signin.AccessToken, signin.RefreshToken,
                credit, birth, signin.Raw, DateTime.UtcNow);
            registry.Complete(cred.Username, cred.Password, result);
            return Apply(cred, result);
        }
        catch
        {
            registry.Release(cred.Username, cred.Password);
            return cred;
        }
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
