using DataVortex.Core.Passculture;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataVortex.Tests;

public sealed class CaptchaSolverTests
{
    // Both solvers must no-op (return null) with no key configured, without any network call.
    [Fact]
    public async Task CapMonster_with_no_key_returns_null()
    {
        ICaptchaSolver solver = new CapMonsterService("", NullLogger<CapMonsterService>.Instance);
        Assert.Null(await solver.SolveRecaptchaAsync("siteKey", "https://passculture.app/connexion"));
        Assert.Equal(0, solver.RequestCount);
    }

    [Fact]
    public async Task TwoCaptcha_with_no_key_returns_null()
    {
        ICaptchaSolver solver = new TwoCaptchaService("", NullLogger<TwoCaptchaService>.Instance);
        Assert.Null(await solver.SolveRecaptchaAsync("siteKey", "https://passculture.app/connexion"));
        Assert.Equal(0, solver.RequestCount);
    }
}
