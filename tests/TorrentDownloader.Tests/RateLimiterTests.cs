using System.Diagnostics;
using TorrentDownloader.Core.Services;

namespace TorrentDownloader.Tests;

public class RateLimiterTests
{
    [Fact]
    public async Task ThrottleAsync_WithNoLimit_NeverWaits()
    {
        var limiter = new RateLimiter(); // never had SetLimitBytesPerSecond called - 0 = unlimited

        var sw = Stopwatch.StartNew();
        await limiter.ThrottleAsync(10_000_000, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 200, $"Expected no wait, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ThrottleAsync_FirstCallWithinBurstAllowance_DoesNotWait()
    {
        var limiter = new RateLimiter();
        limiter.SetLimitBytesPerSecond(1000);

        var sw = Stopwatch.StartNew();
        await limiter.ThrottleAsync(1000, CancellationToken.None); // exactly the initial burst allowance
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 200, $"Expected no wait for the initial burst, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ThrottleAsync_ExceedingBurstAllowance_WaitsForRefill()
    {
        var limiter = new RateLimiter();
        limiter.SetLimitBytesPerSecond(1000); // burst allowance starts at 1000 bytes

        await limiter.ThrottleAsync(1000, CancellationToken.None); // consumes the entire burst, no wait

        var sw = Stopwatch.StartNew();
        await limiter.ThrottleAsync(1000, CancellationToken.None); // needs a full second to refill
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 800, $"Expected to wait ~1s for refill, only waited {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 2500, $"Wait was unexpectedly long: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ThrottleAsync_SustainedTransfer_AveragesCloseToTheConfiguredLimit()
    {
        const long limitBytesPerSecond = 5000;
        var limiter = new RateLimiter();
        limiter.SetLimitBytesPerSecond(limitBytesPerSecond);

        const int chunkSize = 500;
        const int chunkCount = 30; // 15,000 bytes total

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < chunkCount; i++)
            await limiter.ThrottleAsync(chunkSize, CancellationToken.None);
        sw.Stop();

        var totalBytes = chunkSize * chunkCount;
        // The first second's worth is a free burst (SetLimitBytesPerSecond grants an initial
        // allowance equal to the limit), so only the remainder is actually throttled.
        var expectedSeconds = (double)(totalBytes - limitBytesPerSecond) / limitBytesPerSecond;
        var actualSeconds = sw.Elapsed.TotalSeconds;

        // Generous tolerance for CI/sandbox timing jitter, but tight enough to catch "limit does
        // nothing" (which would finish near-instantly) or "limit is wildly wrong".
        Assert.True(actualSeconds >= expectedSeconds * 0.7, $"Too fast: expected ~{expectedSeconds:F1}s, took {actualSeconds:F1}s");
        Assert.True(actualSeconds <= expectedSeconds * 1.8, $"Too slow: expected ~{expectedSeconds:F1}s, took {actualSeconds:F1}s");
    }

    [Fact]
    public async Task SetLimitBytesPerSecond_ToZero_DisablesThrottlingImmediately()
    {
        var limiter = new RateLimiter();
        limiter.SetLimitBytesPerSecond(100); // very slow
        await limiter.ThrottleAsync(100, CancellationToken.None); // drain the burst

        limiter.SetLimitBytesPerSecond(0); // switch to unlimited

        var sw = Stopwatch.StartNew();
        await limiter.ThrottleAsync(10_000_000, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 200, $"Expected no wait after switching to unlimited, took {sw.ElapsedMilliseconds}ms");
    }
}
