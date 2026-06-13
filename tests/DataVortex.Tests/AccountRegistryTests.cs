using System.Text.Json;
using System.Text.Json.Serialization;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Accounts;
using DataVortex.Core.Models;
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

    private (AppPaths paths, StorageService storage) New()
    {
        var paths = new AppPaths(_dir).EnsureCreated();
        return (paths, new StorageService(paths));
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
    public void LoadAccountsNeedingCredit_returns_only_creditless_successes_with_a_refresh_token()
    {
        var (_, storage) = New();
        var now = DateTime.UtcNow;
        // candidate: success, no credit, has a refresh token
        storage.UpsertAccount(new AccountRecord("k1", "need@x", "p", null, true, 200, "ACTIVE", "VALIDE", null, null, null, now, "acc1", "ref1"));
        // not: credit already known
        storage.UpsertAccount(new AccountRecord("k2", "has@x", "p", null, true, 200, "ACTIVE", "VALIDE", 50m, null, null, now, "acc2", "ref2"));
        // not: no refresh token to reuse
        storage.UpsertAccount(new AccountRecord("k3", "notok@x", "p", null, true, 200, "ACTIVE", "VALIDE", null, null, null, now, "acc3", null));
        // not: a 400 (wrong password) is not a success
        storage.UpsertAccount(new AccountRecord("k4", "bad@x", "p", null, false, 400, null, "INVALIDE", null, null, null, now, null, null));

        var candidates = storage.LoadAccountsNeedingCredit();
        Assert.Single(candidates);
        Assert.Equal("need@x", candidates[0].Email);
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

        var storage = new StorageService(paths);
        var reg = new AccountTestRegistry(paths, storage, NullLogger<AccountTestRegistry>.Instance);

        Assert.True(reg.TryGet("leg@x", "p", out var r));
        Assert.Equal(200, r.StatusCode);
        Assert.False(File.Exists(legacyPath));                       // old file consumed
        Assert.True(File.Exists(legacyPath + ".migrated"));          // and renamed
        Assert.Single(storage.SearchAccounts(null, new[] { "VALIDE" }));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
