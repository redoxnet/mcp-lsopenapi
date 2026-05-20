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

    [Fact]
    public void T3521_GlobalMarketQuote_HasExpectedShape()
    {
        TrMeta meta = TrCatalog.Default.Get("t3521");
        meta.Path.Should().Be("/stock/investinfo");
        meta.Category.Should().Be("투자정보");

        meta.InBlocks.Should().ContainSingle()
            .Which.Fields.Select(f => f.Name).Should().Equal("kind", "symbol");

        TrBlock outBlock = meta.OutBlocks.Should().ContainSingle().Subject;
        outBlock.Name.Should().Be("t3521OutBlock");
        outBlock.IsArray.Should().BeFalse();
        outBlock.Fields.Select(f => f.Name).Should().Equal(
            "date", "symbol", "change", "sign", "diff", "close", "hname");
    }

    [Fact]
    public void T3518_GlobalMarketSeries_ExposesBodyContinuationKeys()
    {
        TrMeta meta = TrCatalog.Default.Get("t3518");
        meta.Path.Should().Be("/stock/investinfo");
        meta.Continuation.Supported.Should().BeTrue();
        meta.Continuation.KeyFields.Should().Equal("cts_date", "cts_time");

        meta.InBlocks.Should().ContainSingle()
            .Which.Fields.Select(f => f.Name).Should().Equal(
                "kind", "symbol", "cnt", "jgbn", "nmin", "cts_date", "cts_time");

        meta.OutBlocks.Select(b => b.Name).Should().Equal("t3518OutBlock", "t3518OutBlock1");
        meta.OutBlocks[1].IsArray.Should().BeTrue();
    }

    [Fact]
    public void T3102_NewsBody_ModelsBodyFragmentsAndTitle()
    {
        TrMeta meta = TrCatalog.Default.Get("t3102");
        meta.Path.Should().Be("/stock/investinfo");

        meta.InBlocks.Should().ContainSingle()
            .Which.Fields.Select(f => f.Name).Should().Equal("sNewsno");

        meta.OutBlocks.Select(b => b.Name).Should().Equal("t3102OutBlock", "t3102OutBlock1", "t3102OutBlock2");
        meta.OutBlocks[0].IsArray.Should().BeTrue();
        meta.OutBlocks[1].IsArray.Should().BeTrue();
        meta.OutBlocks[2].Fields.Select(f => f.Name).Should().Contain("sTitle");
    }

    [Theory]
    [InlineData("t1105", "/stock/market-data", false)]
    [InlineData("t1305", "/stock/market-data", true)]
    [InlineData("t1403", "/stock/etc", true)]
    [InlineData("t1442", "/stock/market-data", true)]
    [InlineData("t1475", "/stock/market-data", true)]
    [InlineData("t1927", "/stock/etc", true)]
    [InlineData("t3401", "/stock/investinfo", true)]
    [InlineData("t8425", "/stock/sector", false)]
    [InlineData("t8428", "/stock/investinfo", true)]
    public void G1StagedTrs_HaveExpectedPathAndContinuation(string code, string path, bool paginated)
    {
        TrMeta meta = TrCatalog.Default.Get(code);
        meta.Path.Should().Be(path);
        meta.Continuation.Supported.Should().Be(paginated);
    }

    [Fact]
    public void T1442_NewHighLow_ModelsScreenerFiltersAndIdxPaging()
    {
        TrMeta meta = TrCatalog.Default.Get("t1442");

        meta.InBlocks.Should().ContainSingle()
            .Which.Fields.Select(f => f.Name).Should().Equal(
                "gubun", "type1", "type2", "type3", "jc_num",
                "sprice", "eprice", "volume", "idx", "jc_num2");

        meta.OutBlocks.Select(b => b.Name).Should().Equal("t1442OutBlock", "t1442OutBlock1");
        meta.OutBlocks[1].IsArray.Should().BeTrue();
        meta.OutBlocks[1].Fields.Select(f => f.Name).Should().Contain(
            new[] { "shcode", "hname", "price", "pastprice", "pastdiff" });
        meta.Continuation.KeyFields.Should().Equal("idx");
    }

    [Fact]
    public void T3401_AnalystOpinions_ModelsBeforeAfterColumns()
    {
        TrMeta meta = TrCatalog.Default.Get("t3401");

        meta.OutBlocks.Select(b => b.Name).Should().Equal("t3401OutBlock", "t3401OutBlock1");
        TrBlock opinions = meta.OutBlocks[1];
        opinions.IsArray.Should().BeTrue();
        opinions.Fields.Select(f => f.Name).Should().Contain(
            new[] { "tradname", "bopn", "nopn", "boga", "noga" });
        meta.Continuation.KeyFields.Should().Equal("cts_date");
    }

    [Fact]
    public void T1305_PeriodPrices_CarryInvestorFlowPerBar()
    {
        TrMeta meta = TrCatalog.Default.Get("t1305");

        meta.OutBlocks.Select(b => b.Name).Should().Equal("t1305OutBlock", "t1305OutBlock1");
        TrBlock bars = meta.OutBlocks[1];
        bars.IsArray.Should().BeTrue();
        bars.Fields.Select(f => f.Name).Should().Contain(
            new[] { "open", "high", "low", "close", "fpvolume", "covolume", "ppvolume", "chdegree" });
        meta.Continuation.KeyFields.Should().Equal("date");
    }
}
