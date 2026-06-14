using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Passculture;

/// <summary>
/// CapMonster Cloud solver. Uses the Anti-Captcha-style JSON API: <c>POST createTask</c> with a
/// <c>RecaptchaV2TaskProxyless</c> task, then polls <c>POST getTaskResult</c> until <c>status=ready</c> and
/// returns <c>solution.gRecaptchaResponse</c>. A drop-in alternative to <see cref="TwoCaptchaService"/>.
/// </summary>
public sealed class CapMonsterService : ICaptchaSolver
{
    private const int MaxAttempts = 24;
    private const int PollIntervalMs = 3000;

    private readonly string _apiKey;
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.capmonster.cloud/") };
    private readonly ILogger<CapMonsterService> _log;
    private int _requestCount;

    public int RequestCount => Volatile.Read(ref _requestCount);
    public event Action<int>? RequestCountChanged;

    public CapMonsterService(string apiKey, ILogger<CapMonsterService> log)
    {
        _apiKey = apiKey ?? string.Empty;
        _log = log;
    }

    public async Task<string?> SolveRecaptchaAsync(string siteKey, string pageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey)) { _log.LogWarning("CapMonster: aucune clé API configurée"); return null; }
        try
        {
            var createBody = JsonSerializer.Serialize(new
            {
                clientKey = _apiKey,
                task = new { type = "RecaptchaV2TaskProxyless", websiteURL = pageUrl, websiteKey = siteKey }
            });
            _log.LogInformation("CapMonster: soumission d'un recaptcha…");
            using var createResp = await _http.PostAsync("createTask",
                new StringContent(createBody, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
            var cs = await createResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var cdoc = JsonDocument.Parse(cs);
            var croot = cdoc.RootElement;
            if (croot.TryGetProperty("errorId", out var eid) && eid.GetInt32() != 0)
            {
                _log.LogWarning("CapMonster: createTask refusé: {Response}", cs);
                return null;
            }
            if (!croot.TryGetProperty("taskId", out var tid)) return null;
            var taskId = tid.GetInt64();

            var n = Interlocked.Increment(ref _requestCount);
            RequestCountChanged?.Invoke(n);
            _log.LogInformation("CapMonster: tâche créée (id={Id}) — demande #{Count}, attente de résolution…", taskId, n);

            var getBody = JsonSerializer.Serialize(new { clientKey = _apiKey, taskId });
            for (int i = 0; i < MaxAttempts; i++)
            {
                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
                using var gResp = await _http.PostAsync("getTaskResult",
                    new StringContent(getBody, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
                var gs = await gResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var gdoc = JsonDocument.Parse(gs);
                var groot = gdoc.RootElement;

                if (groot.TryGetProperty("errorId", out var geid) && geid.GetInt32() != 0)
                {
                    _log.LogWarning("CapMonster: getTaskResult erreur (id={Id}): {Response}", taskId, gs);
                    return null;
                }
                if (groot.TryGetProperty("status", out var stt) && stt.GetString() == "ready")
                {
                    if (groot.TryGetProperty("solution", out var sol) && sol.TryGetProperty("gRecaptchaResponse", out var tok))
                    {
                        _log.LogInformation("CapMonster: résolu (id={Id})", taskId);
                        return tok.GetString();
                    }
                    return null;
                }
                // status == "processing" → keep polling
            }
            _log.LogWarning("CapMonster: timeout (id={Id}) après {Max} tentatives", taskId, MaxAttempts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "CapMonster: exception pendant la résolution");
        }
        return null;
    }
}
