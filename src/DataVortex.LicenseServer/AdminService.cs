using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DataVortex.LicenseServer;

/// <summary>Admin authentication: seeds the first SuperAdmin from configuration, then logs admins in with
/// password + TOTP and issues an opaque session token (cached, role-bearing) that the endpoints authorise against.</summary>
public sealed class AdminService
{
    private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(8);

    private readonly LicenseDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AdminService> _log;

    public AdminService(LicenseDbContext db, IMemoryCache cache, ILogger<AdminService> log)
    {
        _db = db;
        _cache = cache;
        _log = log;
    }

    /// <summary>Creates the initial SuperAdmin from <c>Admin:Email</c>/<c>Admin:Password</c> if no admin exists.
    /// The generated TOTP secret is logged ONCE so the operator can enrol it in an authenticator app.</summary>
    public async Task SeedAsync(string? email, string? password)
    {
        if (await _db.Admins.AnyAsync()) return;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;

        var secret = AdminAuth.GenerateTotpSecret();
        _db.Admins.Add(new Admin
        {
            Email = email,
            PasswordHash = AdminAuth.HashPassword(password),
            TotpSecret = secret,
            Role = AdminRole.SuperAdmin,
        });
        await _db.SaveChangesAsync();

        _log.LogWarning("SuperAdmin créé ({Email}). Secret TOTP à enrôler dans une app d'authentification " +
                        "(affiché une seule fois) : {Secret}", email, secret);
    }

    /// <summary>Verifies password + TOTP and, on success, returns a fresh session token bound to the admin's role.</summary>
    public async Task<string?> LoginAsync(string email, string password, string totp)
    {
        var admin = await _db.Admins.FirstOrDefaultAsync(a => a.Email == email);
        if (admin is null || !AdminAuth.VerifyPassword(password, admin.PasswordHash)) return null;
        if (!AdminAuth.VerifyTotp(admin.TotpSecret, totp)) return null;

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _cache.Set(SessionKey(token), admin.Role, SessionTtl);
        return token;
    }

    /// <summary>Resolves a bearer token to its role, or null if unknown/expired.</summary>
    public AdminRole? RoleFor(string token)
        => _cache.TryGetValue(SessionKey(token), out AdminRole role) ? role : null;

    private static string SessionKey(string token) => "sess:" + token;
}
