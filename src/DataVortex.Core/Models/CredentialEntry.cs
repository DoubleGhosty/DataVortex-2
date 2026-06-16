using System;

namespace DataVortex.Core.Models;

public sealed record CredentialEntry(
    string? Url,
    string? Username,
    string? Password,
    int LineNumber,
    string ContextLine,
    bool Tested = false,
    bool? TestSuccess = null,
    string? TestMessage = null,
    DateTime? TestedUtc = null,
    string? AccessToken = null,
    string? RefreshToken = null,
    decimal? Credit = null,
    string? BirthDate = null,
    int? StatusCode = null,
    string? AccountState = null)
{
    /// <summary>Display category derived from the backend outcome:
    /// 200+ACTIVE = VALIDE, 200+UPON_USER_REQUEST = RECUP, 200+SUSPENDED/DELETED/ANONYMIZED = BAN,
    /// 200+INACTIVE = INACTIVE, other 200 = CUSTOM, 400 = INVALIDE.</summary>
    public string Category => StatusCode switch
    {
        // Kept in sync with AccountTestRegistry.Categorize (RECUP checked before BAN — its strings contain SUSPEND/SUSPICIOUS).
        200 when IsRecoverableState(AccountState) => "RECUP",
        200 when IsBadState(AccountState) => "BAN",
        // Expired opportunity (ex_beneficiary / aged-out) → EXPIRE, even if a stale remaining shows > 0.
        200 when IsExpiredState(AccountState) => "EXPIRE",
        // Usable credit wins over statusType (e.g. an "eligible" underage beneficiary with money to spend) → VALIDE.
        200 when Credit > 0m => "VALIDE",
        // ACTIVE spent to 0 AND 18+ → EXPIRE (no more credit coming); a minor spent to 0 stays VALIDE (grant grows
        // at 18). null credit / unknown age → VALIDE.
        200 when string.Equals(AccountState, "ACTIVE", StringComparison.OrdinalIgnoreCase) && Credit == 0m && IsAdult(BirthDate) => "EXPIRE",
        200 when string.Equals(AccountState, "ACTIVE", StringComparison.OrdinalIgnoreCase) => "VALIDE",
        200 when string.Equals(AccountState, "INACTIVE", StringComparison.OrdinalIgnoreCase) => "INACTIVE",
        200 => "CUSTOM", // incl. still-eligible non_eligible / eligible without credit
        400 when IsRecoverableState(AccountState) => "RECUP",
        400 when IsBadState(AccountState) => "BAN",                // e.g. ACCOUNT_DELETED / ACCOUNT_ANONYMIZED
        400 when !string.IsNullOrEmpty(AccountState) => "CUSTOM",  // e.g. EMAIL_NOT_VALIDATED
        400 => "INVALIDE",                                         // bare 400 = wrong password
        _ => ""
    };

    // hard suspended / suspicious / deleted / anonymized → BAN (mirror of AccountTestRegistry.IsBadState).
    private static bool IsBadState(string? s) => s is not null && (
        s.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("SUSPICIOUS", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("DELET", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("ANONYM", StringComparison.OrdinalIgnoreCase));

    // user-reversible suspensions → RECUP (mirror of AccountTestRegistry.IsRecoverableState).
    private static bool IsRecoverableState(string? s) => s is not null && (
        s.Contains("UPON_USER_REQUEST", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("SUSPICIOUS_LOGIN_REPORTED_BY_USER", StringComparison.OrdinalIgnoreCase));

    // ex_beneficiary or the aged-out non_eligible marker → EXPIRE (mirror of AccountTestRegistry.IsExpiredState).
    private static bool IsExpiredState(string? s) =>
        string.Equals(s, "ex_beneficiary", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(s, "eligibility_expired", StringComparison.OrdinalIgnoreCase);

    // 18+ check (mirror of AccountTestRegistry.IsAdult): unknown/unparseable date → false (never demote on a guess).
    private static bool IsAdult(string? birthDate)
    {
        if (!DateTime.TryParse(birthDate, out var dob)) return false;
        var today = DateTime.UtcNow.Date;
        var age = today.Year - dob.Year;
        if (dob.Date > today.AddYears(-age)) age--;
        return age >= 18;
    }

    /// <summary>Credit shown to the user: the backend value is in cents, so divide by 100 (29000 → 290).</summary>
    public decimal? CreditDisplay => Credit is null ? null : Credit / 100m;
}
