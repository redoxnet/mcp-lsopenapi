using FluentAssertions;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;

/// <summary>
/// Pins that the cl100k_base tokenizer is wired correctly — i.e. the
/// Microsoft.ML.Tokenizers.Data.Cl100kBase package is referenced so
/// <see cref="TokenEstimator.Count"/> resolves offline.
/// </summary>
public class TokenEstimatorTests
{
    [Fact]
    public void Count_KnownAsciiString_MatchesCl100kBase()
    {
        // cl100k_base encodes "hello world" as ["hello", " world"].
        TokenEstimator.Count("hello world").Should().Be(2);
    }

    [Fact]
    public void Count_KoreanJson_ExceedsCharHeuristic()
    {
        // Korean syllables cost more tokens per char than the 3.5 char/token
        // heuristic assumes — this gap is the whole reason §4.1 uses cl100k.
        const string json = """{"종목명":"삼성전자","현재가":71500}""";
        TokenEstimator.Count(json).Should().BeGreaterThan(TokenEstimator.Estimate(json));
    }

    [Fact]
    public void ShouldFitTokenBudget_WithinBudget_Passes()
    {
        """{"ok":true}""".ShouldFitTokenBudget(50);
    }

    [Fact]
    public void ShouldFitTokenBudget_OverBudget_Throws()
    {
        string overBudget = new('가', 500);
        Action act = () => overBudget.ShouldFitTokenBudget(10);
        act.Should().Throw<Xunit.Sdk.XunitException>();
    }
}
