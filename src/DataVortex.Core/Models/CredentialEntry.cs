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
        200 when string.Equals(AccountState, "ACTIVE", StringComparison.OrdinalIgnoreCase) => "VALIDE",
        200 when string.Equals(AccountState, "SUSPICIOUS_LOGIN_REPORTED_BY_USER", StringComparison.OrdinalIgnoreCase) => "BAN",
        200 => "CUSTOM",
        400 => "INVALIDE",
        _ => ""
    };
}
