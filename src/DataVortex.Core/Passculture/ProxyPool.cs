using System.Net;

namespace DataVortex.Core.Passculture;

/// <summary>
/// Holds one <see cref="HttpClient"/> per proxy parsed from "http://user:pass@host:port" lines and hands
/// them out round-robin, so successive Passculture requests go through rotating residential sessions
/// (spreads load across IPs → fewer 429s). A single handler cannot carry different credentials for the same
/// host:port, hence one client per proxy. With an empty/disabled list it falls back to a single direct client.
/// When the backend rate-limits a proxy (HTTP 429), the caller benches it via <see cref="Ban"/> for a cooldown
/// and <see cref="Next"/> skips it until the window elapses, so retries rotate to a fresh IP.
/// </summary>
public sealed class ProxyPool
{
    private readonly HttpClient[] _clients;
    private readonly long[] _bannedUntil; // per-client UTC ticks until which it stays in 429 quarantine (0 = available)
    private int _idx = -1;

    /// <summary>Number of proxied clients (0 when running direct, i.e. no usable proxy).</summary>
    public int ProxyCount { get; }

    public ProxyPool(IEnumerable<string>? proxyLines, Uri baseAddress, bool enabled)
    {
        var clients = new List<HttpClient>();
        if (enabled && proxyLines is not null)
        {
            foreach (var raw in proxyLines)
            {
                var line = raw?.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (!TryParse(line, out var proxyUri, out var cred)) continue;
                var handler = new HttpClientHandler
                {
                    Proxy = new WebProxy(proxyUri) { Credentials = cred },
                    UseProxy = true
                };
                clients.Add(new HttpClient(handler) { BaseAddress = baseAddress });
            }
        }

        ProxyCount = clients.Count;
        if (clients.Count == 0)
            clients.Add(new HttpClient { BaseAddress = baseAddress }); // direct fallback (no/disabled proxies)
        _clients = clients.ToArray();
        _bannedUntil = new long[_clients.Length];
    }

    /// <summary>Returns the next client in round-robin order, skipping any still in 429 quarantine. Thread-safe.
    /// If every client is benched (heavy rate-limiting), falls back to the next one anyway — better to try than
    /// to stall.</summary>
    public HttpClient Next()
    {
        int n = _clients.Length;
        long now = DateTime.UtcNow.Ticks;
        for (int probe = 0; probe < n; probe++)
        {
            int idx = (int)((uint)Interlocked.Increment(ref _idx) % (uint)n);
            if (Volatile.Read(ref _bannedUntil[idx]) <= now)
                return _clients[idx];
        }
        return _clients[(int)((uint)Interlocked.Increment(ref _idx) % (uint)n)];
    }

    /// <summary>Benches the proxy behind <paramref name="client"/> for <paramref name="duration"/> after a
    /// rate-limit (HTTP 429): <see cref="Next"/> skips it until the window elapses. No-op in direct mode — there
    /// is no other client to fall back to, so benching the lone direct client would only stall every request.</summary>
    public void Ban(HttpClient client, TimeSpan duration)
    {
        if (ProxyCount == 0) return;
        int idx = Array.IndexOf(_clients, client);
        if (idx < 0) return;
        Volatile.Write(ref _bannedUntil[idx], DateTime.UtcNow.Add(duration).Ticks);
    }

    /// <summary>Proxies not currently in 429 quarantine (for diagnostics/logging).</summary>
    public int AvailableCount
    {
        get
        {
            long now = DateTime.UtcNow.Ticks;
            int free = 0;
            foreach (var until in _bannedUntil) if (until <= now) free++;
            return free;
        }
    }

    /// <summary>Parses "scheme://user:pass@host:port" into a userinfo-free proxy Uri + credentials.
    /// Returns false (skip the line) if it isn't a valid absolute URI.</summary>
    private static bool TryParse(string line, out Uri proxyUri, out NetworkCredential? cred)
    {
        proxyUri = null!;
        cred = null;
        if (!Uri.TryCreate(line, UriKind.Absolute, out var u)) return false;

        var userInfo = u.UserInfo;
        if (!string.IsNullOrEmpty(userInfo))
        {
            var i = userInfo.IndexOf(':');
            var user = i >= 0 ? userInfo[..i] : userInfo;
            var pass = i >= 0 ? userInfo[(i + 1)..] : "";
            cred = new NetworkCredential(Uri.UnescapeDataString(user), Uri.UnescapeDataString(pass));
        }

        proxyUri = new Uri($"{u.Scheme}://{u.Host}:{u.Port}");
        return true;
    }
}
