using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Passculture;

public sealed class TwoCaptchaService
{
    private readonly string _apiKey;
    private readonly HttpClient _http = new();
    private readonly ILogger<TwoCaptchaService> _log;
    private int _requestCount;

    /// <summary>Total captchas submitted to 2captcha this session.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>Raised with the new running total each time a captcha is submitted to 2captcha.</summary>
    public event Action<int>? RequestCountChanged;

    public TwoCaptchaService(string apiKey, ILogger<TwoCaptchaService> log)
    {
        _apiKey = apiKey ?? string.Empty;
        _http.BaseAddress = new Uri("http://2captcha.com/");
        _log = log;
    }

    /// <summary>
    /// Solve a recaptcha using 2captcha. Returns the token or null if not available/failed.
    /// </summary>
    public async Task<string?> SolveRecaptchaAsync(string siteKey, string pageUrl, int maxAttempts = 24, int pollIntervalMs = 5000, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey)) { _log.LogWarning("2captcha: aucune clé API configurée"); return null; }
        try
        {
            var form = new Dictionary<string, string>
            {
                ["key"] = _apiKey,
                ["method"] = "userrecaptcha",
                ["googlekey"] = siteKey,
                ["pageurl"] = pageUrl,
                ["json"] = "1"
            };
            _log.LogInformation("2captcha: soumission d'un recaptcha…");
            using var resp = await _http.PostAsync("/in.php", new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
            var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var st) && st.GetInt32() == 1 && root.TryGetProperty("request", out var req))
            {
                var id = req.GetString();
                if (string.IsNullOrWhiteSpace(id)) return null;
                var n = Interlocked.Increment(ref _requestCount);
                RequestCountChanged?.Invoke(n);
                _log.LogInformation("2captcha: capté (id={Id}) — demande #{Count}, attente de résolution…", id, n);
                // poll
                for (int i = 0; i < maxAttempts; i++)
                {
                    await Task.Delay(pollIntervalMs, ct).ConfigureAwait(false);
                    var url = $"/res.php?key={_apiKey}&action=get&id={id}&json=1";
                    using var r2 = await _http.GetAsync(url, ct).ConfigureAwait(false);
                    var s2 = await r2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var d2 = JsonDocument.Parse(s2);
                    var root2 = d2.RootElement;
                    if (root2.TryGetProperty("status", out var st2))
                    {
                        var status = st2.GetInt32();
                        if (status == 1 && root2.TryGetProperty("request", out var req2))
                        {
                            _log.LogInformation("2captcha: résolu (id={Id})", id);
                            return req2.GetString();
                        }
                        if (status == 0 && root2.TryGetProperty("request", out var err))
                        {
                            var e = err.GetString();
                            if (!string.Equals(e, "CAPCHA_NOT_READY", StringComparison.OrdinalIgnoreCase))
                            {
                                _log.LogWarning("2captcha: échec (id={Id}): {Error}", id, e);
                                return null; // error
                            }
                        }
                    }
                }
                _log.LogWarning("2captcha: timeout (id={Id}) après {Max} tentatives", id, maxAttempts);
            }
            else
            {
                _log.LogWarning("2captcha: soumission refusée: {Response}", s);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "2captcha: exception pendant la résolution");
        }
        return null;
    }
}
