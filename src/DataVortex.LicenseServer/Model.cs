using System.Text.Json.Serialization;
using DataVortex.Licensing;

namespace DataVortex.LicenseServer;

// ------------------------------------------------------------------ entities

/// <summary>Stored status of a licence (distinct from the client's runtime state).</summary>
public enum LicenseState { Active, Suspended, Revoked, Expired }

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string? Company { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class License
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Hash of the licence key — the key itself is shown once and never stored.</summary>
    public string KeyHash { get; set; } = "";
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public LicenseType Type { get; set; }
    public LicenseState Status { get; set; } = LicenseState.Active;
    public int MaxActivations { get; set; } = 1;
    /// <summary>Comma-separated feature flags.</summary>
    public string Features { get; set; } = "";
    public int FingerprintTolerancePercent { get; set; } = 60;
    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public List<Activation> Activations { get; set; } = new();
}

public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FingerprintHash { get; set; } = "";
    /// <summary>The per-component value hashes (JSON) — used for the authoritative fuzzy match at verify time.</summary>
    public string ComponentsJson { get; set; } = "[]";
    public DateTimeOffset FirstSeen { get; set; } = DateTimeOffset.UtcNow;
}

public class Activation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LicenseId { get; set; }
    public License? License { get; set; }
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset ActivatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LeaseExpiresAt { get; set; }
    public string? Ip { get; set; }
}

public class AuthLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? LicenseId { get; set; }
    public string Action { get; set; } = "";
    public string Result { get; set; } = "";
    public string? Ip { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public class SigningKeyRecord
{
    public string Kid { get; set; } = "";
    public string PublicKeySpki { get; set; } = "";
    /// <summary>MVP: the private key is stored here. PRODUCTION must move this to a KMS/HSM.</summary>
    public string PrivateKeyPkcs8 { get; set; } = "";
    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Admin privilege tiers (ordered): Support can read + revoke, Admin can also issue/suspend/reset,
/// SuperAdmin manages everything. RBAC checks use <c>role &gt;= required</c>.</summary>
public enum AdminRole { Support = 0, Admin = 1, SuperAdmin = 2 }

public class Admin
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    /// <summary>PBKDF2 hash (salt.hash, base64).</summary>
    public string PasswordHash { get; set; } = "";
    /// <summary>Base32 TOTP secret (RFC 6238 second factor).</summary>
    public string TotpSecret { get; set; } = "";
    public AdminRole Role { get; set; } = AdminRole.Admin;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// ------------------------------------------------------------------ HTTP DTOs (snake_case to match the client)

public sealed record ComponentDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("h")] string? H,
    [property: JsonPropertyName("w")] int W);

public sealed record FingerprintDto(
    [property: JsonPropertyName("components")] List<ComponentDto>? Components);

public sealed record ActivateDto(
    [property: JsonPropertyName("license_key")] string? LicenseKey,
    [property: JsonPropertyName("fingerprint")] FingerprintDto? Fingerprint,
    [property: JsonPropertyName("app_version")] string? AppVersion);

public sealed record VerifyDto(
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("fingerprint")] FingerprintDto? Fingerprint);

public sealed record TokenDto([property: JsonPropertyName("token")] string? Token);

public sealed record GenerateLicenseDto(
    string Email, string? Company, string Type, int MaxActivations,
    string[]? Features, int? ValidityDays, int? FingerprintTolerancePercent);

public sealed record LoginDto(string Email, string Password, string Totp);

/// <summary>Matches what the client's HttpLicenseApiClient reads: <c>status</c> (a LicenseServerStatus name),
/// an optional signed <c>token</c>, and an optional <c>message</c>.</summary>
public sealed record ApiResponse(string status, string? token = null, string? message = null);
