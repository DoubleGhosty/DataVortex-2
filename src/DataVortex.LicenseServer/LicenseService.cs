using System.Text;
using System.Text.Json;
using DataVortex.Licensing;
using Microsoft.EntityFrameworkCore;

namespace DataVortex.LicenseServer;

/// <summary>The licence business logic — the single authority for activation, verification, renewal, deactivation
/// and the admin operations. Every sensitive decision (status, slots, fingerprint match) is made here, server-side.</summary>
public sealed class LicenseService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromDays(14);

    private readonly LicenseDbContext _db;
    private readonly SigningService _signing;

    public LicenseService(LicenseDbContext db, SigningService signing)
    {
        _db = db;
        _signing = signing;
    }

    // ------------------------------------------------------------------ public flows

    public async Task<ApiResponse> ActivateAsync(ActivateDto dto, string? ip)
    {
        if (string.IsNullOrWhiteSpace(dto.LicenseKey) || dto.Fingerprint is null)
            return new("InvalidKey", message: "requête invalide");

        var hash = LicenseKeys.NormalizeAndHash(dto.LicenseKey);
        var lic = await _db.Licenses.Include(l => l.Activations).FirstOrDefaultAsync(l => l.KeyHash == hash);
        if (lic is null) { await LogAsync(null, "activate", "invalid_key", ip); return new("InvalidKey", message: "clé de licence invalide"); }

        var statusError = StatusError(lic);
        if (statusError is not null) { await LogAsync(lic.Id, "activate", statusError.ToLowerInvariant(), ip); return new(statusError); }

        var snapshot = ToSnapshot(dto.Fingerprint);
        var device = await GetOrCreateDeviceAsync(snapshot);

        // Idempotent on (licence, device): a retried activation never burns a second slot.
        var activation = lic.Activations.FirstOrDefault(a => a.Active && a.DeviceId == device.Id);
        if (activation is null)
        {
            if (lic.Activations.Count(a => a.Active) >= lic.MaxActivations)
            {
                await LogAsync(lic.Id, "activate", "activation_limit", ip);
                return new("ActivationLimit", message: "nombre maximal d'activations atteint pour cette clé");
            }
            activation = new Activation { LicenseId = lic.Id, DeviceId = device.Id };
            _db.Activations.Add(activation);
        }
        activation.Active = true;
        activation.LastSeenAt = DateTimeOffset.UtcNow;
        activation.LeaseExpiresAt = DateTimeOffset.UtcNow + LeaseDuration;
        activation.Ip = ip;

        await _db.SaveChangesAsync();
        await LogAsync(lic.Id, "activate", "ok", ip);
        return new("Ok", token: IssueToken(lic, device));
    }

    public async Task<ApiResponse> VerifyAsync(VerifyDto dto, string? ip)
    {
        var (lic, error) = await ResolveAsync(dto.Token);
        if (error is not null) return error;

        var statusError = StatusError(lic!);
        if (statusError is not null) { await LogAsync(lic!.Id, "verify", statusError.ToLowerInvariant(), ip); return new(statusError); }

        var activation = lic!.Activations.FirstOrDefault(a => a.Active);
        var device = activation is null ? null : await _db.Devices.FindAsync(activation.DeviceId);

        // Authoritative fuzzy hardware re-check (when the client sent a fingerprint and we have a bound device).
        if (dto.Fingerprint is not null && device is not null)
        {
            var score = ToSnapshot(dto.Fingerprint).MatchScore(FromJson(device.ComponentsJson));
            if (score < lic.FingerprintTolerancePercent / 100.0)
            {
                await LogAsync(lic.Id, "verify", "hardware_mismatch", ip);
                return new("HardwareMismatch", message: "cette licence est liée à une autre machine");
            }
        }

        if (activation is not null)
        {
            activation.LastSeenAt = DateTimeOffset.UtcNow;
            activation.LeaseExpiresAt = DateTimeOffset.UtcNow + LeaseDuration;
            await _db.SaveChangesAsync();
        }
        await LogAsync(lic.Id, "verify", "ok", ip);
        return new("Ok", token: IssueToken(lic, device));
    }

    public Task<ApiResponse> RenewAsync(TokenDto dto, string? ip) => VerifyAsync(new VerifyDto(dto.Token, null), ip);

    public async Task<ApiResponse> DeactivateAsync(TokenDto dto, string? ip)
    {
        var (lic, error) = await ResolveAsync(dto.Token);
        if (error is not null) return new("Ok"); // deactivation is best-effort; never surface an error to the client

        var verifier = await _signing.VerifierAsync();
        var claims = verifier.Verify(dto.Token!).Claims;
        var acts = await _db.Activations.Where(a => a.LicenseId == lic!.Id && a.Active).ToListAsync();
        foreach (var a in acts)
        {
            var d = await _db.Devices.FindAsync(a.DeviceId);
            if (d is not null && d.FingerprintHash == claims?.FingerprintHash) a.Active = false;
        }
        await _db.SaveChangesAsync();
        await LogAsync(lic!.Id, "deactivate", "ok", ip);
        return new("Ok");
    }

    // ------------------------------------------------------------------ admin

    public async Task<(string key, License license)> GenerateAsync(GenerateLicenseDto dto)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == dto.Email)
                   ?? new User { Email = dto.Email, Company = dto.Company };
        if (_db.Entry(user).State == EntityState.Detached) _db.Users.Add(user);

        var key = LicenseKeys.Generate();
        var license = new License
        {
            KeyHash = LicenseKeys.NormalizeAndHash(key),
            User = user,
            UserId = user.Id,
            Type = Enum.TryParse<LicenseType>(dto.Type, ignoreCase: true, out var t) ? t : LicenseType.Trial,
            MaxActivations = dto.MaxActivations <= 0 ? 1 : dto.MaxActivations,
            Features = string.Join(",", dto.Features ?? Array.Empty<string>()),
            FingerprintTolerancePercent = dto.FingerprintTolerancePercent is > 0 and <= 100 ? dto.FingerprintTolerancePercent.Value : 60,
            ExpiresAt = dto.ValidityDays is > 0 ? DateTimeOffset.UtcNow.AddDays(dto.ValidityDays.Value) : null,
        };
        _db.Licenses.Add(license);
        await _db.SaveChangesAsync();
        return (key, license);
    }

    public async Task<bool> SetStatusAsync(Guid licenseId, LicenseState status)
    {
        var lic = await _db.Licenses.FindAsync(licenseId);
        if (lic is null) return false;
        lic.Status = status;
        if (status == LicenseState.Revoked)
            foreach (var a in await _db.Activations.Where(a => a.LicenseId == licenseId).ToListAsync()) a.Active = false;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ResetActivationsAsync(Guid licenseId)
    {
        var acts = await _db.Activations.Where(a => a.LicenseId == licenseId).ToListAsync();
        foreach (var a in acts) a.Active = false;
        await _db.SaveChangesAsync();
        return acts.Count > 0;
    }

    /// <summary>Licence list for the dashboard, filtered by a licence id (GUID) or an e-mail substring.</summary>
    public async Task<object> SearchAsync(string? query)
    {
        var q = _db.Licenses.Include(l => l.User).Include(l => l.Activations).AsQueryable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            query = query.Trim();
            if (Guid.TryParse(query, out var gid)) q = q.Where(l => l.Id == gid);
            else q = q.Where(l => l.User != null && l.User.Email.Contains(query));
        }
        var items = await q.OrderByDescending(l => l.IssuedAt).Take(200).ToListAsync();
        return items.Select(l => new
        {
            id = l.Id,
            email = l.User != null ? l.User.Email : "",
            type = l.Type.ToString(),
            status = l.Status.ToString(),
            maxActivations = l.MaxActivations,
            activeActivations = l.Activations.Count(a => a.Active),
            issuedAt = l.IssuedAt,
            expiresAt = l.ExpiresAt,
        });
    }

    public async Task<object> StatsAsync()
    {
        var licenses = await _db.Licenses.ToListAsync();
        return new
        {
            total = licenses.Count,
            active = licenses.Count(l => l.Status == LicenseState.Active),
            suspended = licenses.Count(l => l.Status == LicenseState.Suspended),
            revoked = licenses.Count(l => l.Status == LicenseState.Revoked),
            expired = licenses.Count(l => l.Status == LicenseState.Expired),
            byType = licenses.GroupBy(l => l.Type.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            activeActivations = await _db.Activations.CountAsync(a => a.Active),
        };
    }

    public async Task<object?> DetailAsync(Guid id)
    {
        var lic = await _db.Licenses.Include(l => l.User).Include(l => l.Activations).FirstOrDefaultAsync(l => l.Id == id);
        if (lic is null) return null;

        var deviceIds = lic.Activations.Select(a => a.DeviceId).Distinct().ToList();
        var devices = await _db.Devices.Where(d => deviceIds.Contains(d.Id)).ToListAsync();
        var logs = await _db.AuthLogs.Where(x => x.LicenseId == id).OrderByDescending(x => x.At).Take(50).ToListAsync();

        return new
        {
            id = lic.Id,
            email = lic.User?.Email,
            type = lic.Type.ToString(),
            status = lic.Status.ToString(),
            maxActivations = lic.MaxActivations,
            features = lic.Features,
            issuedAt = lic.IssuedAt,
            expiresAt = lic.ExpiresAt,
            activations = lic.Activations.Select(a => new { a.Id, a.DeviceId, a.Active, a.ActivatedAt, a.LastSeenAt, a.LeaseExpiresAt, a.Ip }),
            devices = devices.Select(d => new { d.Id, d.FingerprintHash, d.FirstSeen }),
            logs = logs.Select(x => new { x.Action, x.Result, x.Ip, x.At }),
        };
    }

    public async Task<string> ExportCsvAsync()
    {
        var licenses = await _db.Licenses.Include(l => l.User).Include(l => l.Activations).ToListAsync();
        var sb = new StringBuilder();
        sb.AppendLine("id,email,type,status,maxActivations,activeActivations,issuedAt,expiresAt");
        foreach (var l in licenses)
            sb.Append(l.Id).Append(',').Append(Csv(l.User?.Email)).Append(',').Append(l.Type).Append(',')
              .Append(l.Status).Append(',').Append(l.MaxActivations).Append(',').Append(l.Activations.Count(a => a.Active))
              .Append(',').Append(l.IssuedAt.ToString("o")).Append(',').Append(l.ExpiresAt?.ToString("o") ?? "").Append('\n');
        return sb.ToString();
    }

    private static string Csv(string? s)
        => string.IsNullOrEmpty(s) ? "" : (s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s);

    // ------------------------------------------------------------------ helpers

    /// <summary>Verifies a returned token's signature and resolves its licence. Returns an error response when the
    /// token is unauthentic or the licence is gone.</summary>
    private async Task<(License? license, ApiResponse? error)> ResolveAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return (null, new("ServerError", message: "requête invalide"));
        var verifier = await _signing.VerifierAsync();
        var res = verifier.Verify(token);
        if (!res.Valid || res.Claims is null || !Guid.TryParse(res.Claims.LicenseId, out var id))
            return (null, new("ServerError", message: "jeton non authentifié"));

        var lic = await _db.Licenses.Include(l => l.Activations).FirstOrDefaultAsync(l => l.Id == id);
        return lic is null ? (null, new("Revoked", message: "licence introuvable")) : (lic, null);
    }

    private string IssueToken(License lic, Device? device)
    {
        var claims = new LicenseClaims
        {
            LicenseId = lic.Id.ToString(),
            Type = lic.Type,
            Features = lic.Features.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            FingerprintHash = device?.FingerprintHash ?? "",
            IssuedAt = DateTimeOffset.UtcNow,
            LeaseExpiresAt = DateTimeOffset.UtcNow + LeaseDuration,
            LicenseExpiresAt = lic.ExpiresAt,
        };
        return _signing.Sign(claims);
    }

    private static string? StatusError(License lic)
    {
        if (lic.Status == LicenseState.Revoked) return "Revoked";
        if (lic.Status == LicenseState.Suspended) return "Suspended";
        if (lic.Status == LicenseState.Expired) return "Expired";
        if (lic.ExpiresAt is { } exp && DateTimeOffset.UtcNow >= exp) return "Expired";
        return null;
    }

    private async Task<Device> GetOrCreateDeviceAsync(FingerprintSnapshot snapshot)
    {
        var hash = snapshot.Hash;
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.FingerprintHash == hash);
        if (device is null)
        {
            device = new Device { FingerprintHash = hash, ComponentsJson = ToJson(snapshot) };
            _db.Devices.Add(device);
            await _db.SaveChangesAsync();
        }
        return device;
    }

    private async Task LogAsync(Guid? licenseId, string action, string result, string? ip)
    {
        _db.AuthLogs.Add(new AuthLog { LicenseId = licenseId, Action = action, Result = result, Ip = ip });
        await _db.SaveChangesAsync();
    }

    private static FingerprintSnapshot ToSnapshot(FingerprintDto dto)
        => new((dto.Components ?? new List<ComponentDto>()).Select(c => new ComponentHash(c.Id ?? "", c.H ?? "", c.W)));

    private static string ToJson(FingerprintSnapshot s)
        => JsonSerializer.Serialize(s.Components.Select(c => new { id = c.Id, h = c.ValueHash, w = c.Weight }));

    private static FingerprintSnapshot FromJson(string json)
    {
        var list = new List<ComponentHash>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var c in doc.RootElement.EnumerateArray())
                    list.Add(new ComponentHash(
                        c.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        c.TryGetProperty("h", out var h) ? h.GetString() ?? "" : "",
                        c.TryGetProperty("w", out var w) && w.TryGetInt32(out var wi) ? wi : 0));
        }
        catch { /* malformed → empty snapshot (match fails closed) */ }
        return new FingerprintSnapshot(list);
    }
}
