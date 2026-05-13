using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Catalog;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Catalog;

public class TrCatalogTests
{
    [Fact]
    public void Default_LoadsEmbeddedCatalog()
    {
        TrCatalog catalog = TrCatalog.Default;

        catalog.All.Should().NotBeEmpty();
        catalog.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Find_KnownCode_ReturnsEntry()
    {
        TrMeta? quote = TrCatalog.Default.Find("t1101");

        quote.Should().NotBeNull();
        quote!.Name.Should().Contain("현재가");
        quote.Path.Should().Be("/stock/market-data");
        quote.InBlocks.Should().HaveCount(1);
        quote.InBlocks[0].Name.Should().Be("t1101InBlock");
        quote.InBlocks[0].Fields.Should().Contain(f => f.Name == "shcode" && f.Required);
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        TrCatalog.Default.Find("T1101").Should().NotBeNull();
        TrCatalog.Default.Find("T8410").Should().NotBeNull();
    }

    [Fact]
    public void Find_UnknownCode_ReturnsNull()
    {
        TrCatalog.Default.Find("nope").Should().BeNull();
    }

    [Fact]
    public void Get_UnknownCode_Throws()
    {
        Action act = () => TrCatalog.Default.Get("nope");
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void Search_ByKoreanKeyword_FindsByName()
    {
        IReadOnlyList<TrMeta> results = TrCatalog.Default.Search("현재가");
        results.Should().NotBeEmpty();
        results.Should().Contain(t => t.TrCode == "t1101");
    }

    [Fact]
    public void Search_ByTrCode_PrefersExactCodeMatch()
    {
        IReadOnlyList<TrMeta> results = TrCatalog.Default.Search("t8410");
        results.Should().NotBeEmpty();
        results[0].TrCode.Should().Be("t8410");
    }

    [Fact]
    public void Search_ByCategory_ReturnsRelatedTrs()
    {
        IReadOnlyList<TrMeta> results = TrCatalog.Default.Search("차트");
        results.Should().Contain(t => t.TrCode == "t8410");
        results.Should().Contain(t => t.TrCode == "t8412");
    }

    [Fact]
    public void Search_LimitRespected()
    {
        IReadOnlyList<TrMeta> results = TrCatalog.Default.Search("주식", limit: 2);
        results.Should().HaveCountLessThanOrEqualTo(2);
    }

    [Fact]
    public void Search_EmptyKeyword_ReturnsEmpty()
    {
        TrCatalog.Default.Search("").Should().BeEmpty();
        TrCatalog.Default.Search("   ").Should().BeEmpty();
    }

    [Fact]
    public void Continuation_IsModeledCorrectly()
    {
        TrMeta chart = TrCatalog.Default.Get("t8410");
        chart.Continuation.Supported.Should().BeTrue();
        chart.Continuation.KeyFields.Should().NotBeNull();
        chart.Continuation.KeyFields!.Should().Equal("cts_date");

        TrMeta minute = TrCatalog.Default.Get("t8412");
        minute.Continuation.Supported.Should().BeTrue();
        minute.Continuation.KeyFields!.Should().Equal("cts_date", "cts_time");

        TrMeta quote = TrCatalog.Default.Get("t1101");
        quote.Continuation.Supported.Should().BeFalse();
    }

    [Fact]
    public void OutBlocks_ArrayFlag_IsSet()
    {
        TrMeta chart = TrCatalog.Default.Get("t8410");
        TrBlock candles = chart.OutBlocks.Single(b => b.Name == "t8410OutBlock1");
        candles.IsArray.Should().BeTrue();
        candles.Fields.Should().Contain(f => f.Name == "close");
    }

    [Fact]
    public void FromContent_ParsesMinimalCatalog()
    {
        const string json = """
            {
              "version": "test",
              "generated_at_utc": "2026-01-01T00:00:00Z",
              "source": "test",
              "trs": [
                {
                  "tr_code": "tX",
                  "name": "Test TR",
                  "category": "Test",
                  "path": "/test",
                  "description": "Test",
                  "in_blocks": [],
                  "out_blocks": [],
                  "continuation": { "supported": false, "key_field": null }
                }
              ]
            }
            """;

        TrCatalog catalog = TrCatalog.FromContent(json);
        catalog.All.Should().HaveCount(1);
        catalog.Get("tX").Name.Should().Be("Test TR");
    }
}
