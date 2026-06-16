using System.Text;
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

    /// <summary>Scans a text file on disk (used when extracted *.txt are persisted).</summary>
    public static IReadOnlyList<CredentialEntry> ScanFile(string path)
    {
        if (!File.Exists(path)) return Array.Empty<CredentialEntry>();
        try { return Scan(File.ReadAllLines(path)); }
        catch { return Array.Empty<CredentialEntry>(); }
    }

    /// <summary>Scans a text stream without ever touching disk — the in-memory extraction path. The stream
    /// is read fully; the caller owns its lifetime.</summary>
    public static IReadOnlyList<CredentialEntry> ScanStream(Stream stream)
    {
        var lines = new List<string>();
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16, leaveOpen: true);
            string? line;
            while ((line = reader.ReadLine()) is not null) lines.Add(line);
        }
        catch { return Array.Empty<CredentialEntry>(); }
        return Scan(lines);
    }

    private static IReadOnlyList<CredentialEntry> Scan(IReadOnlyList<string> lines)
    {
        var list = new List<CredentialEntry>();
        ScanLabeledBlocks(lines, list);
        ScanUlpLines(lines, list);
        return list;
    }

    /// <summary>Stealer-log format: a line containing the anchor keyword 'passculture', then 'Username:' /
    /// 'Password:' labelled lines in the following few lines.</summary>
    private static void ScanLabeledBlocks(IReadOnlyList<string> lines, List<CredentialEntry> list)
    {
        for (int i = 0; i < lines.Count; i++)
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

            for (int j = i + 1; j < Math.Min(lines.Count, i + 11); j++)
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
                var contextLine = (i + 1) < lines.Count ? lines[i + 1] ?? string.Empty : string.Empty;
                list.Add(new CredentialEntry(url, username, password, i + 1, contextLine));
            }
        }
    }

    /// <summary>ULP combolist format: <c>url:login:password</c> per line. We keep ONLY passculture URLs (the chosen
    /// scope). The URL and the password may themselves contain ':' and '@' (android deep-links like
    /// <c>android://&lt;token&gt;==@app.passculture.webapp/</c>, symbol-heavy passwords), so we take the LAST
    /// ':'-field as the password and the second-to-last as the login — robust against both.</summary>
    private static void ScanUlpLines(IReadOnlyList<string> lines, List<CredentialEntry> list)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var cred = ParseUlp(lines[i], i + 1);
            if (cred is not null) list.Add(cred);
        }
    }

    /// <summary>Parses one ULP line, returning a credential only for a <b>passculture URL</b> with an email login.
    /// Returns null otherwise (non-passculture URL, missing fields, non-email login). A password that itself
    /// contains ':' is not supported (inherent ULP ambiguity) — rare in practice.</summary>
    public static CredentialEntry? ParseUlp(string? raw, int lineNumber = 0)
    {
        if (raw is null) return null;
        var line = raw.Trim();
        if (line.Length == 0) return null;
        // Cheap pre-filter (scope = passculture URLs only) before the split.
        if (line.IndexOf("passculture", StringComparison.OrdinalIgnoreCase) < 0) return null;

        var parts = line.Split(':');
        if (parts.Length < 3) return null; // need at least url:login:password

        var password = parts[^1];
        var login = parts[^2].Trim();
        var url = string.Join(':', parts[..^2]);

        if (password.Length == 0 || !LooksLikeEmail(login)) return null;
        // The passculture token must be in the URL part — not merely in the email domain or the password.
        if (url.IndexOf("passculture", StringComparison.OrdinalIgnoreCase) < 0) return null;

        return new CredentialEntry(url, login, password, lineNumber, line);
    }

    /// <summary>Loose email shape check: <c>local@domain.tld</c>, exactly one '@', no spaces.</summary>
    private static bool LooksLikeEmail(string s)
    {
        var at = s.IndexOf('@');
        if (at <= 0 || at != s.LastIndexOf('@')) return false;
        var dot = s.IndexOf('.', at + 1);
        return dot > at + 1 && dot < s.Length - 1 && !s.Contains(' ');
    }
}
