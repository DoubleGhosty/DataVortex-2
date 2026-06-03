using System.Text.RegularExpressions;
using DataVortex.Core.Models;

namespace DataVortex.Core.Extraction;

public static class CredentialScanner
{
    // Look for the anchor keyword 'passculture' (case-insensitive) and then search subsequent lines
    // for username and password fields. Returns found credentials.
    private static readonly Regex UsernameRegex = new(@"^(?:\s*(?:Username|Login|User|Identifiant)\s*[:\-\t]?\s*)(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PasswordRegex = new(@"^(?:\s*(?:Password|Pass|Mot de passe)\s*[:\-\t]?\s*)(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"https?://[^\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<CredentialEntry> ScanFile(string path)
    {
        var list = new List<CredentialEntry>();
        if (!File.Exists(path)) return list;

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return list; }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line == null) continue;
            if (!line.Contains("passculture", StringComparison.OrdinalIgnoreCase)) continue;

            // Found anchor; scan next up to 10 lines for username and password
            string? url = null;
            var mUrl = UrlRegex.Match(line);
            if (mUrl.Success) url = mUrl.Value;

            string? username = null;
            string? password = null;

            for (int j = i + 1; j < Math.Min(lines.Length, i + 11); j++)
            {
                var l = lines[j];
                if (l == null) continue;
                if (username == null)
                {
                    var mu = UsernameRegex.Match(l);
                    if (mu.Success) username = mu.Groups[1].Value.Trim();
                }
                if (password == null)
                {
                    var mp = PasswordRegex.Match(l);
                    if (mp.Success) password = mp.Groups[1].Value.Trim();
                }
                if (url == null)
                {
                    var mu2 = UrlRegex.Match(l);
                    if (mu2.Success) url = mu2.Value;
                }
                if (username != null && password != null) break;
            }

            if (username != null || password != null)
            {
                // Use the first line after anchor that has content as context
                var contextLine = (i + 1) < lines.Length ? lines[i + 1] ?? string.Empty : string.Empty;
                list.Add(new CredentialEntry(url, username, password, i + 1, contextLine));
            }
        }

        return list;
    }
}
