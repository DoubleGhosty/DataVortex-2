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
    /// 200+ACTIVE = VALIDE, 200+SUSPICIOUS_LOGIN_REPORTED_BY_USER = BAN, other 200 = CUSTOM, 400 = INVALIDE.</summary>
    public string Category => StatusCode switch
    {
        // Kept in sync with AccountTestRegistry.Categorize.
        200 when IsBadState(AccountState) => "BAN",
        200 when string.Equals(AccountState, "ACTIVE", StringComparison.OrdinalIgnoreCase) => "VALIDE",
        200 => "CUSTOM",
        400 when IsBadState(AccountState) => "BAN",                // e.g. ACCOUNT_DELETED
        400 when !string.IsNullOrEmpty(AccountState) => "CUSTOM",  // e.g. EMAIL_NOT_VALIDATED
        400 => "INVALIDE",                                         // bare 400 = wrong password
        _ => ""
    };

    // suspended / suspicious / deleted → BAN (mirror of AccountTestRegistry.IsBadState).
    private static bool IsBadState(string? s) => s is not null && (
        s.Contains("SUSPEND", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("SUSPICIOUS", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("DELET", StringComparison.OrdinalIgnoreCase));

    /// <summary>Credit shown to the user: the backend value is in cents, so divide by 100 (29000 → 290).</summary>
    public decimal? CreditDisplay => Credit is null ? null : Credit / 100m;
}
