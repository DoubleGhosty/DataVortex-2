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
        200 when string.Equals(AccountState, "ACTIVE", StringComparison.OrdinalIgnoreCase) => "VALIDE",
        200 when string.Equals(AccountState, "INACTIVE", StringComparison.OrdinalIgnoreCase) => "INACTIVE",
        200 when string.Equals(AccountState, "ex_beneficiary", StringComparison.OrdinalIgnoreCase) => "EXPIRE",
        200 => "CUSTOM", // incl. non_eligible
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

    /// <summary>Credit shown to the user: the backend value is in cents, so divide by 100 (29000 → 290).</summary>
    public decimal? CreditDisplay => Credit is null ? null : Credit / 100m;
}
