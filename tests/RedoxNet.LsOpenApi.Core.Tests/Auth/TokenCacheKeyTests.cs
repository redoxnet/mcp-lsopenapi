using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Auth;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Auth;

public class TokenCacheKeyTests
{
    [Fact]
    public void Create_ProducesStableKey()
    {
        string first = TokenCacheKey.Create("PS-key", LsMarket.Real);
        string second = TokenCacheKey.Create("PS-key", LsMarket.Real);
        first.Should().Be(second);
    }

    [Fact]
    public void Create_KeyDoesNotContainRawAppKey()
    {
        string key = TokenCacheKey.Create("super-secret-app-key", LsMarket.Real);
        key.Should().NotContain("super-secret-app-key");
    }

    [Fact]
    public void Create_DifferentMarkets_ProduceDifferentKeys()
    {
        string real = TokenCacheKey.Create("PS-key", LsMarket.Real);
        string virtual_ = TokenCacheKey.Create("PS-key", LsMarket.Virtual);
        real.Should().NotBe(virtual_);
    }

    [Fact]
    public void Create_DifferentAppKeys_ProduceDifferentKeys()
    {
        string a = TokenCacheKey.Create("PS-a", LsMarket.Real);
        string b = TokenCacheKey.Create("PS-b", LsMarket.Real);
        a.Should().NotBe(b);
    }

    [Fact]
    public void Create_KeyEndsWithMarketSuffix()
    {
        TokenCacheKey.Create("PS-key", LsMarket.Real).Should().EndWith(":real");
        TokenCacheKey.Create("PS-key", LsMarket.Virtual).Should().EndWith(":virtual");
    }

    [Fact]
    public void Create_EmptyAppKey_Throws()
    {
        Action act = () => TokenCacheKey.Create(string.Empty, LsMarket.Real);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_FromCredentialsRecord_MatchesRawCall()
    {
        var creds = new LsCredentials("PS-key", "secret", LsMarket.Virtual);
        TokenCacheKey.Create(creds).Should().Be(TokenCacheKey.Create("PS-key", LsMarket.Virtual));
    }
}
