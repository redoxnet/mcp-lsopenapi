using System.Diagnostics;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Http;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Http;

public class TrRateLimiterTests
{
    [Fact]
    public async Task WaitAsync_RateUnlimited_ReturnsImmediately()
    {
        var limiter = new TrRateLimiter();
        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < 5; i++)
            await limiter.WaitAsync("tr1", ratePerSecond: null);

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(50);
    }

    [Fact]
    public async Task WaitAsync_RateZero_ReturnsImmediately()
    {
        var limiter = new TrRateLimiter();
        Stopwatch sw = Stopwatch.StartNew();

        for (int i = 0; i < 5; i++)
            await limiter.WaitAsync("tr1", ratePerSecond: 0);

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(50);
    }

    [Fact]
    public async Task WaitAsync_RateLimited_SpacesCalls()
    {
        var limiter = new TrRateLimiter();
        Stopwatch sw = Stopwatch.StartNew();

        // 10/sec -> 100ms gap. Three calls should take >= 200ms.
        for (int i = 0; i < 3; i++)
            await limiter.WaitAsync("tr1", ratePerSecond: 10);

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(180);
    }

    [Fact]
    public async Task WaitAsync_DifferentTrs_AreIndependent()
    {
        var limiter = new TrRateLimiter();
        Stopwatch sw = Stopwatch.StartNew();

        // Three calls on different TR codes -> no spacing required.
        await limiter.WaitAsync("tr1", ratePerSecond: 1);
        await limiter.WaitAsync("tr2", ratePerSecond: 1);
        await limiter.WaitAsync("tr3", ratePerSecond: 1);

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(50);
    }

    [Fact]
    public async Task WaitAsync_CancellationHonored()
    {
        var limiter = new TrRateLimiter();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Rate 1/sec -> 1s gap. After first immediate call, the second should block.
        await limiter.WaitAsync("tr1", ratePerSecond: 1);

        Func<Task> act = () => limiter.WaitAsync("tr1", ratePerSecond: 1, cancellationToken: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
