using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Catalog;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Catalog;

/// <summary>
/// Pins the "API용" TR family — entries that LS explicitly labels as preferred
/// for OpenAPI consumption. Catches accidental seed drift if someone edits
/// the embedded JSON.
/// </summary>
public class NewApiTrCatalogTests
{
    [Fact]
    public void T8407_MultiQuote_HasExpectedShape()
    {
        TrMeta meta = TrCatalog.Default.Get("t8407");
        meta.Path.Should().Be("/stock/market-data");
        meta.Category.Should().Be("주식시세");

        meta.InBlocks.Should().ContainSingle()
            .Which.Fields.Select(f => f.Name).Should().Equal("qrycnt", "shcode");

        TrBlock outBlock = meta.OutBlocks.Should().ContainSingle().Subject;
        outBlock.Name.Should().Be("t8407OutBlock1");
        outBlock.IsArray.Should().BeTrue();

        outBlock.Fields.Select(f => f.Name).Should().Contain(new[]
        {
            "shcode", "hname", "price", "sign", "change", "diff", "volume",
            "open", "high", "low", "uplmtprice", "dnlmtprice",
            "offerho", "bidho", "offerrem", "bidrem",
            "totofferrem", "totbidrem", "chdegree",
        });
    }

    [Fact]
    public void T9945_SlimMaster_HasFiveFieldsOnly()
    {
        TrMeta meta = TrCatalog.Default.Get("t9945");
        meta.Path.Should().Be("/stock/market-data");

        TrBlock outBlock = meta.OutBlocks.Should().ContainSingle().Subject;
        outBlock.Name.Should().Be("t9945OutBlock");
        outBlock.IsArray.Should().BeTrue();

        outBlock.Fields.Select(f => f.Name).Should().Equal(
            "hname", "shcode", "expcode", "etfchk", "filler");
    }

    [Fact]
    public void T8436_RemainsAvailableAlongsideT8430AndT9945()
    {
        // Three different "stock list" TRs co-exist for different use cases.
        TrCatalog.Default.Find("t8430").Should().NotBeNull();
        TrCatalog.Default.Find("t8436").Should().NotBeNull();
        TrCatalog.Default.Find("t9945").Should().NotBeNull();
    }
}
