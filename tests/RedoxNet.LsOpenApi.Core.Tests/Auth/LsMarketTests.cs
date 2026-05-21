using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Auth;

public class LsMarketTests
{
    [Theory]
    [InlineData("real", LsMarket.Real)]
    [InlineData("REAL", LsMarket.Real)]
    [InlineData("prod", LsMarket.Real)]
    [InlineData("production", LsMarket.Real)]
    [InlineData("live", LsMarket.Real)]
    [InlineData("virtual", LsMarket.Virtual)]
    [InlineData("paper", LsMarket.Virtual)]
    [InlineData("mock", LsMarket.Virtual)]
    [InlineData("sandbox", LsMarket.Virtual)]
    [InlineData("test", LsMarket.Virtual)]
    public void Parse_KnownValues(string input, LsMarket expected)
    {
        LsMarketExtensions.Parse(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown-value")]
    public void Parse_MissingOrUnknown_DefaultsToReal(string? input)
    {
        LsMarketExtensions.Parse(input).Should().Be(LsMarket.Real);
    }

    [Fact]
    public void ToCanonical_Real_ReturnsLowercase()
    {
        LsMarket.Real.ToCanonical().Should().Be("real");
    }

    [Fact]
    public void ToCanonical_Virtual_ReturnsLowercase()
    {
        LsMarket.Virtual.ToCanonical().Should().Be("virtual");
    }

    [Fact]
    public void LsApiOptions_DefaultMarket_IsReal()
    {
        new LsApiOptions().Market.Should().Be(LsMarket.Real);
    }
}
