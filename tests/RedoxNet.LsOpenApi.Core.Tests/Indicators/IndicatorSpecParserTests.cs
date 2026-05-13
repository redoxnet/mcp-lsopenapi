using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Indicators;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Indicators;

public class IndicatorSpecParserTests
{
    [Theory]
    [InlineData("ma:12", "ma", 12)]
    [InlineData("MA:5", "ma", 5)]
    [InlineData("ema:26", "ema", 26)]
    [InlineData("rsi:14", "rsi", 14)]
    public void TryParse_SingleArg_Succeeds(string raw, string kind, int period)
    {
        IndicatorSpecParser.TryParse(raw, out IndicatorSpec? spec).Should().BeTrue();
        spec!.Kind.Should().Be(kind);
        spec.Args.Should().Equal(period);
    }

    [Fact]
    public void Parse_Macd_ThreeArgs()
    {
        IndicatorSpec spec = IndicatorSpecParser.Parse("macd:12,26,9");
        spec.Kind.Should().Be("macd");
        spec.Args.Should().Equal(12.0, 26.0, 9.0);
        spec.Raw.Should().Be("macd:12,26,9");
    }

    [Fact]
    public void Parse_BollingerBands_PeriodAndStdDev()
    {
        IndicatorSpec spec = IndicatorSpecParser.Parse("bb:20,2");
        spec.Kind.Should().Be("bb");
        spec.Args.Should().Equal(20.0, 2.0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(":12")]
    [InlineData("ma:")]
    [InlineData("ma:abc")]
    [InlineData("ma:0")]
    [InlineData("macd:12,26")]
    [InlineData("bb:20")]
    [InlineData("bb:20,0")]
    [InlineData("unknown:5")]
    public void TryParse_Invalid_ReturnsFalse(string raw)
    {
        IndicatorSpecParser.TryParse(raw, out IndicatorSpec? spec, out string? error).Should().BeFalse();
        spec.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Parse_Invalid_Throws()
    {
        Action act = () => IndicatorSpecParser.Parse("bogus");
        act.Should().Throw<FormatException>();
    }
}
