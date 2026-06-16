using System.Text.Json;
using System.Text.Json.Serialization;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Accounts;
using DataVortex.Core.Models;
using DataVortex.Core.Passculture;
using DataVortex.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataVortex.Tests;

/// <summary>The account registry now persists per-row to SQLite (no whole-file rewrite) and the store is
/// queryable/paged. These cover persistence-across-restart, category filtering, and the legacy JSON import.</summary>
public sealed class AccountRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dvx_acc_" + Guid.NewGuid().ToString("N"));
    private readonly List<StorageService> _stores = new();

    private StorageService NewStore(AppPaths paths)
    {
        var storage = new StorageService(paths);
        _stores.Add(storage);
        return storage;
    }

    private (AppPaths paths, StorageService storage) New()
    {
        var paths = new AppPaths(_dir).EnsureCreated();
        return (paths, NewStore(paths));
    }

    [Fact]
    public void Complete_persists_per_row_and_survives_a_restart()
    {
        var (paths, storage) = New();
        var reg = new AccountTestRegistry(paths, storage, NullLogger<AccountTestRegistry>.Instance);

        Assert.True(reg.TryReserve("A@X.com", "pw", "url"));
        reg.Complete("A@X.com", "pw", new AccountTestResult(true, 200, AccessToken: "tokA", Credit: 29000m, AccountState: "ACTIVE"));

        // A brand-new registry over the same DB rehydrates from SQLite — the account is still known.
        var reg2 = new AccountTestRegistry(paths, storage, NullLogger<AccountTestRegistry>.Instance);
        Assert.True(reg2.TryGet("a@x.com", "pw", out var r)); // identity is case/space-normalised
        Assert.Equal(200, r.StatusCode);
        Assert.Equal("tokA", r.AccessToken);
        Assert.False(reg2.TryReserve("a@x.com", "pw", "url2")); // already known → never re-tested (no wasted captcha)
    }

    [Fact]
    public void SearchAccounts_filters_by_category_and_counts()
    {
        var (_, storage) = New();
        storage.UpsertAccount(new AccountRecord("k1", "valid@x", "p", null, true, 200, "ACTIVE", "VALIDE", 100m, null, null, DateTime.UtcNow, null, null));
        storage.UpsertAccount(new AccountRecord("k2", "ban@x", "p", null, true, 200, "SUSPICIOUS_LOGIN_REPORTED_BY_USER", "BAN", 0m, null, null, DateTime.UtcNow, null, null));
        storage.UpsertAccount(new AccountRecord("k3", "bad@x", "p", null, false, 400, null, "INVALIDE", null, null, null, DateTime.UtcNow, null, null));

        var valids = storage.SearchAccounts(null, new[] { "VALIDE" });
        Assert.Single(valids);
        Assert.Equal("valid@x", valids[0].Email);

        Assert.Equal(2, storage.CountAccounts(null, new[] { "VALIDE", "BAN" }));
        Assert.Single(storage.SearchAccounts("ban", null));     // email contains-search

        var counts = storage.GetAccountCategoryCounts();
        Assert.Equal(1, counts.First(c => c.Category == "VALIDE").Count);
        Assert.Equal(1, counts.First(c => c.Category == "INVALIDE").Count);
    }

    [Fact]
    public void LoadAccountsToRecheck_returns_every_non_invalid_account_with_a_refresh_token()
    {
        var (_, storage) = New();
        var now = DateTime.UtcNow;
        storage.UpsertAccount(new AccountRecord("k1", "valid@x", "p", null, true, 200, "ACTIVE", "VALIDE", null, null, null, now, "acc1", "ref1"));
        storage.UpsertAccount(new AccountRecord("k2", "credit@x", "p", null, true, 200, "ACTIVE", "VALIDE", 50m, null, null, now, "acc2", "ref2"));
        storage.UpsertAccount(new AccountRecord("k5", "ban@x", "p", null, true, 200, "SUSPENDED", "BAN", null, null, null, now, "acc5", "ref5"));
        storage.UpsertAccount(new AccountRecord("k6", "expire@x", "p", null, true, 200, "ex_beneficiary", "EXPIRE", 0m, null, null, now, "acc6", "ref6"));
        storage.UpsertAccount(new AccountRecord("k3", "notok@x", "p", null, true, 200, "ACTIVE", "VALIDE", null, null, null, now, "acc3", null)); // no token
        storage.UpsertAccount(new AccountRecord("k4", "bad@x", "p", null, false, 400, null, "INVALIDE", null, null, null, now, null, null));     // wrong password

        var candidates = storage.LoadAccountsToRecheck();
        Assert.Equal(4, candidates.Count);
        Assert.Contains(candidates, a => a.Email == "ban@x");        // BAN now rechecked
        Assert.Contains(candidates, a => a.Email == "expire@x");     // EXPIRE too
        Assert.DoesNotContain(candidates, a => a.Email == "notok@x"); // no refresh token
        Assert.DoesNotContain(candidates, a => a.Email == "bad@x");   // INVALIDE excluded
    }

    [Fact]
    public void Legacy_account_tests_json_is_imported_then_renamed()
    {
        var paths = new AppPaths(_dir).EnsureCreated();
        var legacy = new List<AccountEntry>
        {
            new() { Email = "leg@x", Password = "p", Result = new AccountTestResult(true, 200, AccountState: "ACTIVE") }
        };
        var json = JsonSerializer.Serialize(legacy, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
        var legacyPath = Path.Combine(paths.Root, "account-tests.json");
        File.WriteAllText(legacyPath, json);

        var storage = NewStore(paths);
        var reg = new AccountTestRegistry(paths, storage, NullLogger<AccountTestRegistry>.Instance);

        Assert.True(reg.TryGet("leg@x", "p", out var r));
        Assert.Equal(200, r.StatusCode);
        Assert.False(File.Exists(legacyPath));                       // old file consumed
        Assert.True(File.Exists(legacyPath + ".migrated"));          // and renamed
        Assert.Single(storage.SearchAccounts(null, new[] { "VALIDE" }));
    }

    [Fact]
    public void RecategorizeAccounts_reclassifies_stale_rows_from_the_current_rules()
    {
        var (_, storage) = New();
        var now = DateTime.UtcNow;
        // Rows written under the OLD rules: a reversible suspension stored as BAN, an INACTIVE stored as CUSTOM.
        storage.UpsertAccount(new AccountRecord("k1", "recup@x", "p", null, true, 200, "SUSPENDED_UPON_USER_REQUEST", "BAN", null, null, null, now, "a", "r"));
        storage.UpsertAccount(new AccountRecord("k2", "inact@x", "p", null, true, 200, "INACTIVE", "CUSTOM", null, null, null, now, "a", "r"));
        var adultDob = DateTime.UtcNow.AddYears(-20).ToString("yyyy-MM-dd");
        var minorDob = DateTime.UtcNow.AddYears(-15).ToString("yyyy-MM-dd");
        storage.UpsertAccount(new AccountRecord("k4", "spent@x", "p", null, true, 200, "ACTIVE", "VALIDE", 0m, adultDob, null, now, "a", "r")); // adult spent to 0 → EXPIRE
        storage.UpsertAccount(new AccountRecord("k5", "minor@x", "p", null, true, 200, "ACTIVE", "VALIDE", 0m, minorDob, null, now, "a", "r")); // minor spent to 0 → stays VALIDE
        storage.UpsertAccount(new AccountRecord("k3", "ok@x", "p", null, true, 200, "ACTIVE", "VALIDE", 50m, null, null, now, "a", "r")); // has credit → unchanged

        var changed = storage.RecategorizeAccounts(AccountTestRegistry.Categorize);

        Assert.Equal(3, changed); // recup, inactive, spent
        Assert.Single(storage.SearchAccounts(null, new[] { "RECUP" }));
        Assert.Single(storage.SearchAccounts(null, new[] { "INACTIVE" }));
        Assert.Single(storage.SearchAccounts(null, new[] { "EXPIRE" }));
        Assert.Equal(0, storage.RecategorizeAccounts(AccountTestRegistry.Categorize)); // idempotent
    }

    [Fact]
    public void Reset_forgets_all_accounts_so_they_can_be_retested()
    {
        var (paths, storage) = New();
        var reg = new AccountTestRegistry(paths, storage, NullLogger<AccountTestRegistry>.Instance);
        reg.TryReserve("a@x", "p", "u");
        reg.Complete("a@x", "p", new AccountTestResult(true, 200, AccountState: "ACTIVE"));
        Assert.True(reg.TryGet("a@x", "p", out _));
        Assert.Single(storage.LoadAccounts());

        reg.Reset();

        Assert.False(reg.TryGet("a@x", "p", out _));      // forgotten in memory
        Assert.Empty(storage.LoadAccounts());             // and in storage
        Assert.True(reg.TryReserve("a@x", "p", "u"));     // so it can be re-tested from scratch
    }

    [Theory]
    [InlineData("stee")]            // truncated email
    [InlineData("0644367428")]      // phone number
    [InlineData("star wars")]       // scanner noise
    [InlineData("")]                // empty
    public async Task TestOnceAsync_skips_identifiers_without_an_at_sign(string username)
    {
        var (paths, storage) = New();
        var reg = new AccountTestRegistry(paths, storage, NullLogger<AccountTestRegistry>.Instance);
        var client = new PasscultureClient(new ProxyPool(null, new Uri("https://example.test/"), enabled: false));

        var cred = new CredentialEntry(null, username, "Cocotier973@", 0, "");
        var result = await AccountTester.TestOnceAsync(client, reg, cred);

        Assert.False(result.Tested);          // never sent to the backend
        Assert.Equal(0, reg.Count);           // never reserved / stored → no captcha spent
    }

    [Fact]
    public void Apply_preserves_account_state_so_category_is_VALIDE()
    {
        var cred = new CredentialEntry(null, "e@x", "p", 0, "");
        var applied = AccountTester.Apply(cred, new AccountTestResult(true, 200, Credit: 594m, AccountState: "ACTIVE"));

        Assert.Equal("ACTIVE", applied.AccountState);
        Assert.Equal("VALIDE", applied.Category);   // was CUSTOM before AccountState was copied → notifier skipped it
        Assert.Equal(594m, applied.Credit);
    }

    [Theory]
    // Genuine bad-password 400 → definitive (INVALIDE)
    [InlineData("{\"general\":[\"Identifiant ou Mot de passe incorrect\"]}", true)]
    [InlineData("{\"general\":[\"identifiant ou mot de passe incorrect\"]}", true)]
    // 400 from a low captcha trust score (or anything else) → NOT a bad password → must be retried
    [InlineData("{\"token\":[\"The captcha is invalid or its trust score is too low\"]}", false)]
    [InlineData("{\"code\":\"NETWORK_REQUEST_FAILED\"}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsWrongPassword_only_true_for_a_real_password_rejection(string? raw, bool expected)
        => Assert.Equal(expected, AccountTester.IsWrongPassword(raw));

    [Theory]
    [InlineData(200, "ACTIVE", "VALIDE")]
    [InlineData(200, "SUSPENDED", "BAN")]                                   // hard fraud/admin suspension
    [InlineData(200, "SUSPENDED_UPON_USER_REQUEST", "RECUP")]               // user-reversible → recoverable
    [InlineData(200, "SUSPICIOUS_LOGIN_REPORTED_BY_USER", "RECUP")]         // reversible via email link → recoverable
    [InlineData(200, "DELETED", "BAN")]
    [InlineData(200, "ANONYMIZED", "BAN")]                                  // RGPD anonymized = irreversible
    [InlineData(200, "WAITING_FOR_ANONYMIZATION", "BAN")]                   // pending anonymization = blocked
    [InlineData(200, "INACTIVE", "INACTIVE")]                               // generic inactive = own bucket
    [InlineData(200, "SOMETHING_ELSE", "CUSTOM")]
    [InlineData(400, null, "INVALIDE")]                       // bare 400 = wrong password
    [InlineData(400, "EMAIL_NOT_VALIDATED", "CUSTOM")]        // 400 with a non-bad reason code = custom
    [InlineData(400, "ACCOUNT_DELETED", "BAN")]               // 400 with a "deleted" code = ban
    [InlineData(400, "ACCOUNT_ANONYMIZED", "BAN")]            // 400 with an "anonymized" code = ban
    [InlineData(200, "ex_beneficiary", "EXPIRE")]            // /me ex-beneficiary = expired credit
    [InlineData(200, "eligibility_expired", "EXPIRE")]       // aged-out non_eligible (window closed) = expired
    [InlineData(200, "non_eligible", "CUSTOM")]              // still-eligible non_eligible = custom
    [InlineData(200, "eligible", "CUSTOM")]                  // eligible-not-activated (no deposit yet) = custom
    public void Categorize_maps_account_states_to_categories(int code, string? state, string expected)
        => Assert.Equal(expected, AccountTestRegistry.Categorize(code, state));

    [Fact]
    public void Categorize_spent_active_account_expires_only_for_adults()
    {
        var adult = DateTime.UtcNow.AddYears(-20).ToString("yyyy-MM-dd");
        var minor = DateTime.UtcNow.AddYears(-15).ToString("yyyy-MM-dd");
        Assert.Equal("EXPIRE", AccountTestRegistry.Categorize(200, "ACTIVE", 0m, adult));     // 20yo spent → expired
        Assert.Equal("VALIDE", AccountTestRegistry.Categorize(200, "ACTIVE", 0m, minor));     // 15yo spent → stays active (grant grows at 18)
        Assert.Equal("VALIDE", AccountTestRegistry.Categorize(200, "ACTIVE", 0m, null));      // unknown age → don't demote
        Assert.Equal("VALIDE", AccountTestRegistry.Categorize(200, "ACTIVE", 30000m, adult)); // has credit → valid
    }

    [Fact]
    public void Categorize_usable_credit_is_VALIDE_regardless_of_status()
    {
        var adult = DateTime.UtcNow.AddYears(-20).ToString("yyyy-MM-dd");
        // "eligible" underage beneficiary that still has money to spend → VALIDE (credit wins over the eligible status)
        Assert.Equal("VALIDE", AccountTestRegistry.Categorize(200, "eligible", 3225m, adult));
        // same status but no credit → CUSTOM (not yet activated)
        Assert.Equal("CUSTOM", AccountTestRegistry.Categorize(200, "eligible", null, adult));
        // expired deposit wins even if a stale remaining shows > 0 → EXPIRE
        Assert.Equal("EXPIRE", AccountTestRegistry.Categorize(200, "ex_beneficiary", 1000m, adult));
    }

    [Theory]
    [InlineData("ACTIVE", "beneficiary", "ACTIVE")]                 // normal eligible → stays VALIDE
    [InlineData("ACTIVE", "ex_beneficiary", "ex_beneficiary")]     // → EXPIRE
    [InlineData("ACTIVE", "non_eligible", "non_eligible")]         // → CUSTOM
    [InlineData("SUSPENDED", "ex_beneficiary", "SUSPENDED")]       // bad sign-in state wins (BAN)
    [InlineData("SUSPENDED_UPON_USER_REQUEST", "ex_beneficiary", "SUSPENDED_UPON_USER_REQUEST")] // recoverable wins (RECUP)
    [InlineData("ACTIVE", "eligible", "eligible")]                 // eligible-not-activated → CUSTOM marker
    [InlineData("ACTIVE", null, "ACTIVE")]
    public void RefineState_applies_me_status(string? signin, string? me, string? expected)
        => Assert.Equal(expected, AccountTester.RefineState(signin, me));

    [Fact]
    public void RefineState_non_eligible_is_expired_only_when_the_window_has_closed()
    {
        var past = DateTime.UtcNow.AddDays(-1);
        var future = DateTime.UtcNow.AddYears(1);
        Assert.Equal("eligibility_expired", AccountTester.RefineState("ACTIVE", "non_eligible", past));    // aged out → EXPIRE
        Assert.Equal("non_eligible", AccountTester.RefineState("ACTIVE", "non_eligible", future));         // still eligible later → CUSTOM
        Assert.Equal("non_eligible", AccountTester.RefineState("ACTIVE", "non_eligible", null));           // unknown window → CUSTOM
    }

    [Theory]
    [InlineData("{\"code\":\"EMAIL_NOT_VALIDATED\",\"general\":[\"L'email n'a pas été validé.\"]}", "EMAIL_NOT_VALIDATED")]
    [InlineData("{\"code\":\"ACCOUNT_DELETED\",\"general\":[\"Le compte a été supprimé\"]}", "ACCOUNT_DELETED")]
    [InlineData("{\"code\":\"ACCOUNT_ANONYMIZED\",\"general\":[\"Le compte a été anonymisé\"]}", "ACCOUNT_ANONYMIZED")]
    [InlineData("{\"general\":[\"Identifiant ou Mot de passe incorrect\"]}", null)] // wrong password is not a definitive code
    [InlineData("{\"token\":[\"trust score too low\"]}", null)]                     // captcha trust → retry
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Definitive400Code_detects_recognised_definitive_400s(string? raw, string? expected)
        => Assert.Equal(expected, AccountTester.Definitive400Code(raw));

    [Fact]
    public void ParseMe_handles_null_domainsCredit_without_dropping_the_status()
    {
        // Non-beneficiary /me: domainsCredit is JSON null — must NOT crash the parse (the old bug dropped the
        // status → the account was misclassified VALIDE instead of CUSTOM/EXPIRE).
        const string body = """
        {"id":123,"email":"x@y.z","birthDate":"2006-01-16","domainsCredit":null,
         "eligibilityEndDatetime":"2025-01-15T23:00:00Z","status":{"statusType":"non_eligible"}}
        """;
        var me = PasscultureClient.ParseMe(body, 200, isSuccess: true);

        Assert.True(me.Success);
        Assert.Equal("non_eligible", me.StatusType);          // status is read, not lost
        Assert.Null(me.DomainsCreditRemaining);               // no deposit → null, not a crash
        Assert.Equal(new DateTime(2025, 1, 15, 23, 0, 0, DateTimeKind.Utc), me.EligibilityEnd);
    }

    [Fact]
    public void ParseMe_reads_a_beneficiary_with_spent_credit()
    {
        const string body = """
        {"id":1,"email":"a@b.c","domainsCredit":{"all":{"initial":30000,"remaining":0}},"status":{"statusType":"beneficiary"}}
        """;
        var me = PasscultureClient.ParseMe(body, 200, isSuccess: true);
        Assert.True(me.Success);
        Assert.Equal(0m, me.DomainsCreditRemaining);
        Assert.Equal("beneficiary", me.StatusType);
    }

    [Theory]
    [InlineData("<html>blocked by proxy</html>", 200, true)]   // 200 but not JSON (interstitial)
    [InlineData("{\"code\":\"FORBIDDEN\"}", 403, false)]       // error JSON, non-200
    [InlineData("{\"foo\":\"bar\"}", 200, true)]               // JSON 200 but not a /me (no id/email)
    [InlineData("", 200, true)]                                 // empty body
    public void ParseMe_rejects_non_me_bodies(string body, int code, bool isSuccess)
        => Assert.False(PasscultureClient.ParseMe(body, code, isSuccess).Success);

    public void Dispose()
    {
        foreach (var s in _stores) { try { s.Dispose(); } catch { /* ignore */ } }
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
