namespace DataVortex.Core.Passculture;

/// <summary>A reCAPTCHA v2 solver that returns a usable <c>g-recaptcha-response</c> token. Implemented by
/// <see cref="TwoCaptchaService"/> and <see cref="CapMonsterService"/>; the active one is chosen by the
/// <c>CaptchaProvider</c> setting, so the rest of the app is provider-agnostic.</summary>
public interface ICaptchaSolver
{
    /// <summary>Solves a reCAPTCHA v2 and returns the token, or null if it failed / no key is configured.</summary>
    Task<string?> SolveRecaptchaAsync(string siteKey, string pageUrl, CancellationToken ct = default);

    /// <summary>Total captchas submitted this session.</summary>
    int RequestCount { get; }

    /// <summary>Raised with the new running total each time a captcha is submitted.</summary>
    event Action<int>? RequestCountChanged;
}
