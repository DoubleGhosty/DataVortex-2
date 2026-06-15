using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DataVortex.App.Services;

/// <summary>
/// Opens an installed Chrome already logged into passculture by injecting the JWTs into the site's
/// <c>localStorage</c> via the DevTools Protocol (no extra dependency — just the BCL WebSocket client).
/// Chrome is launched as an independent process with its own profile, controlled only long enough to inject
/// the token, then left open for the user.
/// </summary>
public static class BrowserSessionLauncher
{
    private const string Origin = "https://passculture.app/";

    public static async Task OpenAsync(string accessToken, string? refreshToken, CancellationToken ct = default)
    {
        var chrome = FindChrome() ?? throw new InvalidOperationException("Chrome introuvable sur ce poste.");
        var userDataDir = Path.Combine(Path.GetTempPath(), "dvx_chrome_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDataDir);

        var psi = new ProcessStartInfo { FileName = chrome, UseShellExecute = false };
        foreach (var a in new[]
        {
            "--remote-debugging-port=0",        // pick a free port, written to DevToolsActivePort
            "--remote-allow-origins=*",         // required since Chrome 111 to accept the CDP WebSocket
            $"--user-data-dir={userDataDir}",   // isolated profile → won't touch the user's normal Chrome
            "--no-first-run",
            "--no-default-browser-check",
            "about:blank"
        }) psi.ArgumentList.Add(a);
        Process.Start(psi);

        var port = await ReadDevToolsPortAsync(Path.Combine(userDataDir, "DevToolsActivePort"), ct).ConfigureAwait(false);
        var pageWs = await GetPageWebSocketAsync(port, ct).ConfigureAwait(false);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(pageWs), ct).ConfigureAwait(false);

        // Run before any page script: when we then navigate to passculture, this sets the tokens in its
        // own-origin localStorage before the app boots → the SPA reads them and is already signed in.
        var script =
            $"localStorage.setItem('access_token', {Js(accessToken)});" +
            $"localStorage.setItem('PASSCULTURE_REFRESH_TOKEN', {Js(refreshToken ?? string.Empty)});";

        await SendAsync(ws, 1, "Page.enable", null, ct).ConfigureAwait(false);
        await SendAsync(ws, 2, "Page.addScriptToEvaluateOnNewDocument", new { source = script }, ct).ConfigureAwait(false);
        await SendAsync(ws, 3, "Page.navigate", new { url = Origin }, ct).ConfigureAwait(false);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct).ConfigureAwait(false); // Chrome stays open
    }

    private static string Js(string s) => JsonSerializer.Serialize(s); // safe, escaped JS string literal

    private static async Task<int> ReadDevToolsPortAsync(string portFile, CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(portFile))
            {
                try
                {
                    var lines = File.ReadAllLines(portFile);
                    if (lines.Length >= 1 && int.TryParse(lines[0], out var port)) return port;
                }
                catch { /* file still being written */ }
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("Chrome DevTools n'a pas démarré à temps.");
    }

    private static async Task<string> GetPageWebSocketAsync(int port, CancellationToken ct)
    {
        using var http = new HttpClient();
        for (var i = 0; i < 50; i++)
        {
            try
            {
                var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json", ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                foreach (var t in doc.RootElement.EnumerateArray())
                {
                    if (t.TryGetProperty("type", out var type) && type.GetString() == "page"
                        && t.TryGetProperty("webSocketDebuggerUrl", out var url))
                        return url.GetString()!;
                }
            }
            catch { /* not ready yet */ }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("Aucun onglet Chrome contrôlable trouvé.");
    }

    private static async Task SendAsync(ClientWebSocket ws, int id, string method, object? prms, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { id, method, @params = prms ?? new { } });
        await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        // Drain frames until the response matching this id arrives (skip CDP events).
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var sb = new StringBuilder();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
            } while (!res.EndOfMessage);

            using var doc = JsonDocument.Parse(sb.ToString());
            if (doc.RootElement.TryGetProperty("id", out var idEl) && idEl.GetInt32() == id) return;
        }
    }

    private static string? FindChrome()
    {
        foreach (var hive in new[] { "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE" })
        {
            if (Registry.GetValue($@"{hive}\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", null, null)
                    is string p && File.Exists(p))
                return p;
        }
        foreach (var p in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
        })
            if (File.Exists(p)) return p;

        return null;
    }
}
