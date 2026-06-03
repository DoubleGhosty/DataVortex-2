namespace DataVortex.Core.Extraction;

/// <summary>
/// Case-insensitive keyword filter applied to <b>filenames only</b>. A name matches if it contains any
/// configured keyword as a substring (so "password" also matches Password / PASSWORD / passwords).
/// File content is intentionally not inspected — deciding on the name alone means non-matching archive
/// entries are never decompressed.
/// </summary>
public sealed class KeywordMatcher
{
    private readonly string[] _keywordsLower;

    public bool Enabled { get; }

    public KeywordMatcher(bool enabled, IEnumerable<string>? keywords)
    {
        _keywordsLower = (keywords ?? Enumerable.Empty<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();
        Enabled = enabled && _keywordsLower.Length > 0;
    }

    /// <summary>True if <paramref name="text"/> (a filename) contains any keyword, case-insensitively.</summary>
    public bool MatchesText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var lower = text.ToLowerInvariant();
        foreach (var keyword in _keywordsLower)
            if (lower.Contains(keyword, StringComparison.Ordinal)) return true;
        return false;
    }
}
