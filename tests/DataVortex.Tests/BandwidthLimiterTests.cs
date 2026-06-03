using System.Diagnostics;
using DataVortex.Core.Pipeline;
using Xunit;

namespace DataVortex.Tests;

public class BandwidthLimiterTests
{
    [Fact]
    public async Task Unlimited_does_not_block()
    {
        var limiter = new BandwidthLimiter(0);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++)
            await limiter.ThrottleAsync(1_000_000, default);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 250, $"took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Limited_rate_introduces_delay()
    {
        // 1 MB/s bucket starts full; consuming 3 MB must wait for ~2 s of refill.
        var limiter = new BandwidthLimiter(1_000_000);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 30; i++)
            await limiter.ThrottleAsync(100_000, default); // 3 MB total
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds > 800, $"took {sw.ElapsedMilliseconds}ms");
    }
}
