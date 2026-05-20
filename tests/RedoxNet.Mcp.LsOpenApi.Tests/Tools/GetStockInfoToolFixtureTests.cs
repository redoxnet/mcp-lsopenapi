using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Catalog;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Pins <see cref="GetStockInfoTool"/> against a real LS testbed-console
/// response for TR <c>t1102</c> (LS증권, shcode 078020, captured 2026-05-13).
/// </summary>
/// <remarks>
/// t1102 ("주식 현재가(시세)조회") is the analyst-oriented sister of t1101.
/// It carries PER/PBR/EPS, the two most recent settled-period financials,
/// year-over-year growth rates, 52-week + YTD price ranges, and top-5
/// brokerage flow on both sides in a single ~160-field OutBlock.
/// </remarks>
public class GetStockInfoToolFixtureTests
{
    const string TestbedT1102Response = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "정상적으로 조회가 완료되었습니다.",
      "t1102OutBlock": {
        "hname": "LS증권", "shcode": "078020",
        "price": 4535, "sign": "2", "change": 10, "diff": "000.22",
        "volume": 6929, "jnilvolume": 32336, "volumediff": 25407, "value": 31,
        "open": 4550, "high": 4600, "low": 4535,
        "opentime": "090030", "hightime": "092645", "lowtime": "100906",
        "uplmtprice": 5880, "dnlmtprice": 3170,
        "svi_uplmtprice": 5010, "svi_dnlmtprice": 4095,
        "recprice": 4525, "subprice": 3160, "parprice": 5000,
        "avg": 4555, "jvolume": 6899, "exhratio": "0.78",
        "high52w": 7110, "high52wdate": "20220607",
        "low52w": 4135, "low52wdate": "20230328",
        "highyear": 5480, "highyeardate": "20230202",
        "lowyear": 4135, "lowyeardate": "20230328",
        "per": "011.14", "t_per": "014.75", "pbrx": "000.26",
        "bfsales": 605, "bfsales2": 2116,
        "bfoperatingincome": 257, "bfoperatingincome2": 416,
        "bfordinaryincome": 240, "bfordinaryincome2": 405,
        "bfnetincome": 150, "bfnetincome2": 296,
        "bfeps": "206.51", "bfeps2": "406.95",
        "netrt": "-32.49", "epsrt": "-32.50", "ordrt": "-21.61", "opert": "-18.43",
        "name": "2303 1분기", "name2": "2212 결산", "gsmm": "12",
        "listing": 55481, "capital": 2774, "total": 2516, "jkrate": 40,
        "listdate": "20070221", "memedan": "00001",
        "issueprice": 0, "target": 0, "tonghwa": "KRW", "janginfo": "KOSDAQ",
        "spac_gubun": "N", "abnormal_rise_gu": "0", "low_lqdt_gu": "0",
        "alloc_gubun": "",
        "abscnt": 15778, "vol": "000.01", "fwsvl": 109, "fwdvl": 2,
        "ftradmsval": 0, "ftradmsvag": 4543, "ftradmscha": 0, "ftradmsdiff": "001.57",
        "ftradmdval": 0, "ftradmdvag": 4560, "ftradmdcha": 0, "ftradmddiff": "000.03",
        "offerno1": "유안타",   "offerno2": "키움증",   "offerno3": "삼성증",   "offerno4": "KB증권",   "offerno5": "신한투",
        "offernocd1": "024",   "offernocd2": "050",   "offernocd3": "030",   "offernocd4": "017",   "offernocd5": "002",
        "savg1": 4554, "savg2": 4551, "savg3": 4549, "savg4": 4551, "savg5": 4598,
        "svol1": 1824, "svol2": 1647, "svol3": 1017, "svol4": 813,  "svol5": 529,
        "sval1": 8,    "sval2": 7,    "sval3": 5,    "sval4": 4,    "sval5": 2,
        "scha1": 219,  "scha2": 1031, "scha3": 402,  "scha4": 0,    "scha5": 1,
        "sdiff1": "26.32", "sdiff2": "23.77", "sdiff3": "14.68", "sdiff4": "11.73", "sdiff5": "7.63",
        "bidno1": "미래에", "bidno2": "키움증", "bidno3": "KB증권", "bidno4": "삼성증", "bidno5": "한국증",
        "bidnocd1": "005", "bidnocd2": "050", "bidnocd3": "017", "bidnocd4": "030", "bidnocd5": "003",
        "davg1": 4542, "davg2": 4560, "davg3": 4550, "davg4": 4580, "davg5": 4557,
        "dvol1": 1886, "dvol2": 1273, "dvol3": 1261, "dvol4": 1026, "dvol5": 777,
        "dval1": 9,    "dval2": 6,    "dval3": 6,    "dval4": 5,    "dval5": 4,
        "dcha1": 1866, "dcha2": 0,    "dcha3": 0,    "dcha4": 0,    "dcha5": 0,
        "ddiff1": "27.22", "ddiff2": "18.37", "ddiff3": "18.20", "ddiff4": "14.81", "ddiff5": "11.21",
        "salert": "-27.07",
        "alloc_text": "", "shterm_text": "", "ty_text": "", "lend_text": "",
        "info1": "", "info2": "", "info3": "", "info4": "", "info5": ""
      }
    }
    """;

    static Task<HttpResponseMessage> Ok(string body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

    [Fact]
    public async Task GetStockInfo_DispatchesT1102()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(TestbedT1102Response));

        await GetStockInfoTool.GetStockInfo(client, "078020");

        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/stock/market-data");
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1102");

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"shcode\":\"078020\"");
    }

    [Fact]
    public async Task GetStockInfo_IdentityAndSnapshot()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT1102Response));

        string result = await GetStockInfoTool.GetStockInfo(client, "078020");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("name").GetString().Should().Be("LS증권");
        root.GetProperty("market").GetString().Should().Be("KOSDAQ");
        root.GetProperty("currency").GetString().Should().Be("KRW");
        root.GetProperty("listing_date").GetString().Should().Be("20070221");

        JsonElement snap = root.GetProperty("snapshot");
        snap.GetProperty("price").GetInt64().Should().Be(4535);
        snap.GetProperty("change_percent").GetDouble().Should().BeApproximately(0.22, 0.01);
        // turnover_ratio_percent reads `vol` (회전율) — not `exhratio` (소진율).
        snap.GetProperty("turnover_ratio_percent").GetDouble().Should().BeApproximately(0.01, 0.001);
    }

    [Fact]
    public async Task GetStockInfo_PeriodsSection_52wAndYtd()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT1102Response));

        string result = await GetStockInfoTool.GetStockInfo(client, "078020", new[] { "periods" });
        JsonElement periods = JsonDocument.Parse(result).RootElement.GetProperty("periods");

        periods.GetProperty("week52").GetProperty("high").GetInt64().Should().Be(7110);
        periods.GetProperty("week52").GetProperty("low").GetInt64().Should().Be(4135);
        periods.GetProperty("week52").GetProperty("high_date").GetString().Should().Be("20220607");

        periods.GetProperty("ytd").GetProperty("high").GetInt64().Should().Be(5480);
        periods.GetProperty("ytd").GetProperty("low").GetInt64().Should().Be(4135);
    }

    [Fact]
    public async Task GetStockInfo_Fundamentals_ParsesStringRatios()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT1102Response));

        string result = await GetStockInfoTool.GetStockInfo(client, "078020");
        JsonElement f = JsonDocument.Parse(result).RootElement.GetProperty("fundamentals");

        // "011.14" → 11.14
        f.GetProperty("per").GetDouble().Should().BeApproximately(11.14, 0.01);
        f.GetProperty("expected_per").GetDouble().Should().BeApproximately(14.75, 0.01);
        f.GetProperty("pbr").GetDouble().Should().BeApproximately(0.26, 0.001);

        // t1102's `name`/`bf*` are the latest settled period (전분기), `name2`/`bf*2` the one before.
        f.GetProperty("latest_period_label").GetString().Should().Be("2303 1분기");
        f.GetProperty("previous_period_label").GetString().Should().Be("2212 결산");

        f.GetProperty("sales_latest").GetInt64().Should().Be(605);
        f.GetProperty("sales_previous").GetInt64().Should().Be(2116);
        f.GetProperty("net_income_latest").GetInt64().Should().Be(150);
        f.GetProperty("eps_latest").GetDouble().Should().BeApproximately(206.51, 0.01);

        JsonElement growth = f.GetProperty("growth_percent");
        growth.GetProperty("net_income").GetDouble().Should().BeApproximately(-32.49, 0.01);
        growth.GetProperty("eps").GetDouble().Should().BeApproximately(-32.50, 0.01);
    }

    [Fact]
    public async Task GetStockInfo_BrokersSection_TopFiveSellersAndBuyers()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT1102Response));

        string result = await GetStockInfoTool.GetStockInfo(client, "078020", new[] { "brokers" });
        JsonElement brokerage = JsonDocument.Parse(result).RootElement.GetProperty("brokers");

        JsonElement sellers = brokerage.GetProperty("sellers");
        sellers.GetArrayLength().Should().Be(5);
        // LS suffix convention: the sell-side broker (offerno) pairs with the
        // d* (매도) fields; the buy-side broker (bidno) with the s* (매수) fields.
        sellers[0].GetProperty("name").GetString().Should().Be("유안타");
        sellers[0].GetProperty("code").GetString().Should().Be("024");
        sellers[0].GetProperty("avg_price").GetInt64().Should().Be(4542);
        sellers[0].GetProperty("volume").GetInt64().Should().Be(1886);

        JsonElement buyers = brokerage.GetProperty("buyers");
        buyers.GetArrayLength().Should().Be(5);
        buyers[0].GetProperty("name").GetString().Should().Be("미래에");
        buyers[0].GetProperty("volume").GetInt64().Should().Be(1824);
        buyers[0].GetProperty("change_percent").GetDouble().Should().BeApproximately(26.32, 0.01);
    }

    [Fact]
    public async Task GetStockInfo_CapitalAndFlagsSections()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT1102Response));

        string result = await GetStockInfoTool.GetStockInfo(client, "078020", new[] { "fundamentals", "flags" });
        JsonElement root = JsonDocument.Parse(result).RootElement;

        // The former top-level `listing` block now nests under fundamentals.capital.
        JsonElement capital = root.GetProperty("fundamentals").GetProperty("capital");
        capital.GetProperty("shares_in_thousands").GetInt64().Should().Be(55481);
        capital.GetProperty("capital_in_100m_won").GetInt64().Should().Be(2774);
        capital.GetProperty("market_cap_in_100m_won").GetInt64().Should().Be(2516);

        JsonElement flags = root.GetProperty("flags");
        flags.GetProperty("is_spac").GetBoolean().Should().BeFalse();
        flags.GetProperty("abnormal_rise").GetString().Should().Be("0");
        flags.GetProperty("low_liquidity").GetString().Should().Be("0");
    }

    // ---------------------- v0.9 §4.3 — pattern A (sections) ----------------------

    [Fact]
    public async Task GetStockInfo_Default_OnlySnapshotAndFundamentals()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT1102Response));

        string result = await GetStockInfoTool.GetStockInfo(client, "078020");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("sections_shown").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("snapshot", "fundamentals");
        root.TryGetProperty("snapshot", out _).Should().BeTrue();
        root.TryGetProperty("fundamentals", out _).Should().BeTrue();
        root.TryGetProperty("periods", out _).Should().BeFalse("unselected sections are omitted");
        root.TryGetProperty("brokers", out _).Should().BeFalse();
        root.TryGetProperty("flags", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetStockInfo_ExplicitSections_EmitsOnlyRequested()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT1102Response));

        string result = await GetStockInfoTool.GetStockInfo(client, "078020", new[] { "snapshot" });
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("sections_shown").EnumerateArray().Should().ContainSingle();
        root.TryGetProperty("snapshot", out _).Should().BeTrue();
        root.TryGetProperty("fundamentals", out _).Should().BeFalse("only the requested section is emitted");
    }

    [Fact]
    public async Task GetStockInfo_SectionsEchoedInCanonicalOrder()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT1102Response));

        // Requested out of order — echo and emission follow canonical order.
        string result = await GetStockInfoTool.GetStockInfo(client, "078020", new[] { "flags", "snapshot" });
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("sections_shown").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("snapshot", "flags");
    }

    [Fact]
    public async Task GetStockInfo_UnknownSection_ReturnsValidationError()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(TestbedT1102Response));

        string result = await GetStockInfoTool.GetStockInfo(client, "078020", new[] { "bogus" });
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().BeEmpty("validation error short-circuits before any TR call");
        root.GetProperty("error").GetString().Should().Contain("bogus");
    }

    [Fact]
    public async Task GetStockInfo_Default_FitsTokenBudget()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT1102Response));

        string result = await GetStockInfoTool.GetStockInfo(client, "078020");

        // Default = snapshot + fundamentals. Measured ~565 tokens (cl100k_base).
        result.ShouldFitTokenBudget(800);
    }

    [Fact]
    public async Task GetStockInfo_AllSections_FitsTokenBudget()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT1102Response));

        string result = await GetStockInfoTool.GetStockInfo(
            client, "078020",
            new[] { "snapshot", "fundamentals", "periods", "brokers", "flags" });

        // All 5 sections (foreign dropped — see SPEC-v0.9 §4.3). Measured ~1,484.
        result.ShouldFitTokenBudget(2000);
    }

    [Fact]
    public void Catalog_T1102_HasExpectedKeyGroups()
    {
        TrMeta meta = TrCatalog.Default.Get("t1102");
        meta.Path.Should().Be("/stock/market-data");
        meta.OutBlocks.Should().ContainSingle();

        IEnumerable<string> fields = meta.OutBlocks[0].Fields.Select(f => f.Name);
        fields.Should().Contain(new[]
        {
            // Fundamentals
            "per", "t_per", "pbrx", "bfsales", "bfeps", "netrt", "epsrt",
            // Ranges
            "high52w", "low52w", "highyear", "lowyear",
            // Brokerage
            "offerno1", "bidno1", "savg1", "davg1",
            // Misc — float shares, turnover, foreign-brokerage flow
            "abscnt", "vol", "ftradmsval", "ftradmdval",
            // Flags
            "spac_gubun", "abnormal_rise_gu", "low_lqdt_gu",
        });
    }
}
