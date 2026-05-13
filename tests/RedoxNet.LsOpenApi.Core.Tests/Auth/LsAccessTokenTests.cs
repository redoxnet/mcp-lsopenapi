using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Auth;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Auth;

public class LsAccessTokenTests
{
    static LsAccessToken NewToken(DateTimeOffset issuedAt, TimeSpan ttl) =>
        new("token", "Bearer", issuedAt, issuedAt + ttl);

    [Fact]
    public void ShouldRefresh_OutsideWindow_ReturnsFalse()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        LsAccessToken token = NewToken(now, TimeSpan.FromHours(1));

        token.ShouldRefresh(TimeSpan.FromMinutes(5), nowUtc: now).Should().BeFalse();
    }

    [Fact]
    public void ShouldRefresh_InsideWindow_ReturnsTrue()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        LsAccessToken token = NewToken(now, TimeSpan.FromMinutes(3));

        token.ShouldRefresh(TimeSpan.FromMinutes(5), nowUtc: now).Should().BeTrue();
    }

    [Fact]
    public void ShouldRefresh_OnBoundary_ReturnsTrue()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        LsAccessToken token = NewToken(now, TimeSpan.FromMinutes(5));

        token.ShouldRefresh(TimeSpan.FromMinutes(5), nowUtc: now).Should().BeTrue();
    }

    [Fact]
    public void IsExpired_PastExpiry_ReturnsTrue()
    {
        DateTimeOffset issued = DateTimeOffset.UtcNow.AddHours(-2);
        LsAccessToken token = NewToken(issued, TimeSpan.FromHours(1));

        token.IsExpired().Should().BeTrue();
    }

    [Fact]
    public void IsExpired_BeforeExpiry_ReturnsFalse()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        LsAccessToken token = NewToken(now, TimeSpan.FromHours(1));

        token.IsExpired(nowUtc: now).Should().BeFalse();
    }
}
