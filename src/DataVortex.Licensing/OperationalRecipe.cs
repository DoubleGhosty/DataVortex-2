using System.Text.Json.Serialization;

namespace DataVortex.Licensing;

/// <summary>The "operational recipe" that makes the Passculture checker work — the exact values an attacker would
/// need to reimplement it: the backend base URL, the reCAPTCHA site-key + page URL, and the endpoint paths. In the
/// hardened design (Palier C) these live ONLY on the server and are delivered per-session as an encrypted blob
/// (<see cref="RecipeCrypto"/>); the client holds them in memory only and never on disk. This type carries just the
/// SHAPE — no real values are embedded in the client binary. Fields default empty so a partial/missing recipe fails
/// closed (the checker can build nothing).</summary>
public sealed record OperationalRecipe
{
    // JSON names are pinned explicitly so the wire format is independent of the C# property names — an obfuscator
    // (Palier D.1) can rename these freely without breaking the sealed recipe's (de)serialisation.

    /// <summary>Backend base URL, e.g. <c>https://backend.passculture.app/</c>.</summary>
    [JsonPropertyName("b")] public string BaseUrl { get; init; } = "";

    /// <summary>reCAPTCHA v2 site-key submitted to the captcha solver.</summary>
    [JsonPropertyName("k")] public string SiteKey { get; init; } = "";

    /// <summary>Page URL paired with the site-key for the captcha solve.</summary>
    [JsonPropertyName("p")] public string PageUrl { get; init; } = "";

    // Relative endpoint paths (joined onto BaseUrl).
    [JsonPropertyName("si")] public string SignInPath { get; init; } = "";
    [JsonPropertyName("rf")] public string RefreshPath { get; init; } = "";
    [JsonPropertyName("un")] public string UnsuspendPath { get; init; } = "";
    [JsonPropertyName("me")] public string MePath { get; init; } = "";

    /// <summary>True only when every field needed to run a check is present — used to fail closed.</summary>
    [JsonIgnore]
    public bool IsComplete =>
        !string.IsNullOrEmpty(SiteKey) && !string.IsNullOrEmpty(PageUrl) &&
        !string.IsNullOrEmpty(SignInPath) && !string.IsNullOrEmpty(RefreshPath);
}
