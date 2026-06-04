using System.Text.RegularExpressions;

namespace DataVortex.Core.Extraction;

/// <summary>
/// Best-effort extraction of an archive password from the Telegram message that accompanies it.
/// Supports many phrasings, languages and separators, e.g. "Password: @JohnDoeSupport", "Mdp : 1234",
/// "mot de passe est : Soleil2024", "🔑 pwd → My_Pass.2024", "PASSWORD - StrongP@ss".
/// Returns <c>null</c> when no plausible password is present.
/// </summary>
public static class PasswordExtractor
{
    private const string Kw =
        @"(?:passwords?|passwort|mot\s*de\s*passe|motdepasse|mdp|pwd|pw|pass|senha|contrase[nñ]a|clave|пароль|key|🔑|🔐)";

    // keyword (+ up to 2 filler words) + a ':' '=' '>' '→' separator + the token
    private static readonly Regex Strong = new(
        Kw + @"(?:[ \t]+\w+){0,2}[ \t*_~]*[:=>→][ \t*_~""'`\[\(<>]*(?<pw>[^\s""'`\]\)>*~]{1,128})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // keyword + dash separator (requires a password-ish token to avoid matching 'password-protected')
    private static readonly Regex Dash = new(
        Kw + @"[ \t*_~]*[-–—][ \t*_~""'`\[\(<>]*(?<pw>[^\s""'`\]\)>*~]{1,128})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "protected","required","locked","is","to","the","for","of","a","an","est","requis",
        "protégé","protege","needed","here","below","above","attached","link","http","https"
    };

    public static string? FromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        foreach (Match m in Strong.Matches(message))
        {
            var pw = Clean(m.Groups["pw"].Value);
            if (Plausible(pw)) return pw;
        }
        foreach (Match m in Dash.Matches(message))
        {
            var pw = Clean(m.Groups["pw"].Value);
            if (Plausible(pw) && Passwordish(pw)) return pw;
        }
        return null;
    }

    private static string Clean(string s) =>
        s.Trim().Trim('"', '\'', '`', '*', '_', '~', '[', ']', '(', ')', '<', '>', '.', ',', ';', ':');

    // Note: a value containing "://" is allowed — in log channels the password is often a t.me link
    // written right after "Password:". The keyword requirement keeps stray links from being captured.
    private static bool Plausible(string s) =>
        s.Length is >= 2 and <= 128 && !Stop.Contains(s);

    private static bool Passwordish(string s) =>
        s.Any(char.IsDigit) || s.Any(c => !char.IsLetterOrDigit(c));
}
