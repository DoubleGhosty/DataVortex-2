using System.Net;

namespace DataVortex.Core.Passculture;

/// <summary>
/// Holds one <see cref="HttpClient"/> per proxy parsed from "http://user:pass@host:port" lines and hands
/// them out round-robin, so successive Passculture requests go through rotating residential sessions
/// (spreads load across IPs → fewer 429s). A single handler cannot carry different credentials for the same
/// host:port, hence one client per proxy. With an empty/disabled list it falls back to a single direct client.
/// </summary>
public sealed class ProxyPool
{
    private readonly HttpClient[] _clients;
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
    }

    /// <summary>Returns the next client in round-robin order. Thread-safe.</summary>
    public HttpClient Next()
        => _clients[(int)((uint)Interlocked.Increment(ref _idx) % (uint)_clients.Length)];

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
