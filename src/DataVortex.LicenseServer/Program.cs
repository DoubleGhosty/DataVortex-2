using System.Text;
using System.Threading.RateLimiting;
using DataVortex.LicenseServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<LicenseDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Licenses")));
builder.Services.AddSingleton<SigningService>();
builder.Services.AddScoped<LicenseService>();
builder.Services.AddScoped<AnomalyService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddMemoryCache();

// Per-IP rate limiting (guards brute force on keys and general API abuse). 120 req/min/IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// On startup: apply pending EF migrations (creates the schema on first run), seed the first admin, and
// initialise the signing key.
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    sp.GetRequiredService<LicenseDbContext>().Database.Migrate();
    await sp.GetRequiredService<AdminService>().SeedAsync(
        builder.Configuration["Admin:Email"], builder.Configuration["Admin:Password"]);
}
await app.Services.GetRequiredService<SigningService>().InitializeAsync();

app.UseRateLimiter();

// Serve the admin dashboard (wwwroot/index.html).
app.UseDefaultFiles();
app.UseStaticFiles();

var hmacKey = Encoding.UTF8.GetBytes(builder.Configuration["Security:AppHmacKey"] ?? "");

// App HMAC + anti-replay on the client endpoints. Skipped when no key is configured (dev); once a key is set the
// server fails closed — an unsigned or replayed request to activate/verify/renew/deactivate is rejected.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    var guarded = path is "/api/v1/activate" or "/api/v1/verify" or "/api/v1/renew" or "/api/v1/deactivate";
    if (guarded && hmacKey.Length > 0)
    {
        ctx.Request.EnableBuffering();
        string body;
        using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true))
            body = await reader.ReadToEndAsync();
        ctx.Request.Body.Position = 0;

        var nonces = ctx.RequestServices.GetRequiredService<IMemoryCache>();
        if (!RequestAuth.Validate(ctx, body, hmacKey, nonces))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new ApiResponse("ServerError", message: "requête non authentifiée"));
            return;
        }
    }
    await next();
});

static string? ClientIp(HttpContext c) => c.Connection.RemoteIpAddress?.ToString();

// RBAC: resolves the bearer session token to a role and checks it meets the minimum for the endpoint.
static bool Authorize(HttpContext c, AdminRole min)
{
    var auth = c.Request.Headers.Authorization.ToString();
    if (!auth.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
    var role = c.RequestServices.GetRequiredService<AdminService>().RoleFor(auth[7..]);
    return role is { } r && r >= min;
}

// ------------------------------------------------------------------ public API
app.MapGet("/api/v1/ping", () => Results.Ok(new { status = "ok", time = DateTimeOffset.UtcNow }));
app.MapGet("/api/v1/keys", async (SigningService s) => Results.Ok(new { keys = await s.PublicKeysAsync() }));

app.MapPost("/api/v1/activate", async (ActivateDto dto, LicenseService svc, HttpContext ctx)
    => Results.Ok(await svc.ActivateAsync(dto, ClientIp(ctx))));
app.MapPost("/api/v1/verify", async (VerifyDto dto, LicenseService svc, HttpContext ctx)
    => Results.Ok(await svc.VerifyAsync(dto, ClientIp(ctx))));
app.MapPost("/api/v1/renew", async (TokenDto dto, LicenseService svc, HttpContext ctx)
    => Results.Ok(await svc.RenewAsync(dto, ClientIp(ctx))));
app.MapPost("/api/v1/deactivate", async (TokenDto dto, LicenseService svc, HttpContext ctx)
    => Results.Ok(await svc.DeactivateAsync(dto, ClientIp(ctx))));

// ------------------------------------------------------------------ admin auth
app.MapPost("/api/v1/admin/login", async (LoginDto dto, AdminService svc) =>
{
    var token = await svc.LoginAsync(dto.Email, dto.Password, dto.Totp);
    return token is null ? Results.Unauthorized() : (IResult)Results.Ok(new { token });
});

// ------------------------------------------------------------------ admin: read + revoke (Support+)
app.MapGet("/api/v1/admin/licenses", async (string? query, LicenseService svc, HttpContext ctx) =>
{
    if (!Authorize(ctx, AdminRole.Support)) return Results.Unauthorized();
    return (IResult)Results.Ok(await svc.SearchAsync(query));
});
app.MapGet("/api/v1/admin/licenses/{id:guid}", async (Guid id, LicenseService svc, HttpContext ctx) =>
{
    if (!Authorize(ctx, AdminRole.Support)) return Results.Unauthorized();
    var detail = await svc.DetailAsync(id);
    return detail is null ? Results.NotFound() : (IResult)Results.Ok(detail);
});
app.MapGet("/api/v1/admin/stats", async (LicenseService svc, HttpContext ctx) =>
{
    if (!Authorize(ctx, AdminRole.Support)) return Results.Unauthorized();
    return (IResult)Results.Ok(await svc.StatsAsync());
});
app.MapGet("/api/v1/admin/anomalies", async (AnomalyService svc, HttpContext ctx) =>
{
    if (!Authorize(ctx, AdminRole.Support)) return Results.Unauthorized();
    return (IResult)Results.Ok(new { anomalies = await svc.DetectSharingAsync() });
});
app.MapGet("/api/v1/admin/export", async (LicenseService svc, HttpContext ctx) =>
{
    if (!Authorize(ctx, AdminRole.Support)) return Results.Unauthorized();
    return (IResult)Results.Text(await svc.ExportCsvAsync(), "text/csv");
});
app.MapPost("/api/v1/admin/licenses/{id:guid}/revoke", async (Guid id, LicenseService svc, HttpContext ctx) =>
{
    if (!Authorize(ctx, AdminRole.Support)) return Results.Unauthorized();
    return await svc.SetStatusAsync(id, LicenseState.Revoked) ? Results.Ok() : (IResult)Results.NotFound();
});

// ------------------------------------------------------------------ admin: issue + lifecycle (Admin+)
app.MapPost("/api/v1/admin/licenses", async (GenerateLicenseDto dto, LicenseService svc, HttpContext ctx) =>
{
    if (!Authorize(ctx, AdminRole.Admin)) return Results.Unauthorized();
    var (key, lic) = await svc.GenerateAsync(dto);
    return (IResult)Results.Ok(new { license_key = key, id = lic.Id, type = lic.Type.ToString() }); // key shown ONCE
});
app.MapPost("/api/v1/admin/licenses/{id:guid}/suspend", async (Guid id, LicenseService svc, HttpContext ctx) =>
{
    if (!Authorize(ctx, AdminRole.Admin)) return Results.Unauthorized();
    return await svc.SetStatusAsync(id, LicenseState.Suspended) ? Results.Ok() : (IResult)Results.NotFound();
});
app.MapPost("/api/v1/admin/licenses/{id:guid}/reactivate", async (Guid id, LicenseService svc, HttpContext ctx) =>
{
    if (!Authorize(ctx, AdminRole.Admin)) return Results.Unauthorized();
    return await svc.SetStatusAsync(id, LicenseState.Active) ? Results.Ok() : (IResult)Results.NotFound();
});
app.MapPost("/api/v1/admin/licenses/{id:guid}/reset", async (Guid id, LicenseService svc, HttpContext ctx) =>
{
    if (!Authorize(ctx, AdminRole.Admin)) return Results.Unauthorized();
    return await svc.ResetActivationsAsync(id) ? Results.Ok() : (IResult)Results.NotFound();
});

app.Run();
