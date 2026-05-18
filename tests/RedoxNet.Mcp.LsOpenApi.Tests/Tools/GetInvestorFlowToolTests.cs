using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public sealed class GetInvestorFlowToolTests
{
    // Trimmed t1601 response with two OutBlocks filled — the wrapper should
    // pick up both and skip the absent OutBlock3..6 quietly.
    const string IntradayTwoSegmentSample = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "OK",
      "t1601OutBlock1": {
        "tjjcode_08": "", "ms_08": 205539, "md_08": 213937, "rate_08": -42, "svolume_08": -8398,
        "jjcode_17": "", "ms_17": 42832, "md_17": 39269, "rate_17": -18, "svolume_17": 3563,
        "jjcode_18": "", "ms_18": 12247, "md_18": 7198, "rate_18": 68, "svolume_18": 5049,
        "jjcode_01": "", "ms_01": 3769, "md_01": 2210, "rate_01": 25, "svolume_01": 1558,
        "jjcode_03": "", "ms_03": 1240, "md_03": 433, "rate_03": 7, "svolume_03": 807,
        "jjcode_04": "", "ms_04": 8, "md_04": 17, "rate_04": 0, "svolume_04": -9,
        "jjcode_02": "", "ms_02": 291, "md_02": 161, "rate_02": 2, "svolume_02": 131,
        "jjcode_05": "", "ms_05": 99, "md_05": 61, "rate_05": 1, "svolume_05": 38,
        "jjcode_06": "", "ms_06": 5928, "md_06": 3978, "rate_06": 26, "svolume_06": 1950,
        "jjcode_11": "", "ms_11": 0, "md_11": 0, "rate_11": 36, "svolume_11": 0,
        "jjcode_07": "", "ms_07": 770, "md_07": 983, "rate_07": -7, "svolume_07": -213,
        "jjcode_00": "", "ms_00": 912, "md_00": 338, "rate_00": 8, "svolume_00": 574
      },
      "t1601OutBlock2": {
        "tjjcode_08": "", "ms_08": 350945, "md_08": 348693, "rate_08": -135, "svolume_08": 2252,
        "jjcode_17": "", "ms_17": 50440, "md_17": 49328, "rate_17": 175, "svolume_17": 1112,
        "jjcode_18": "", "ms_18": 4082, "md_18": 5142, "rate_18": -16, "svolume_18": -1060,
        "jjcode_01": "", "ms_01": 2986, "md_01": 3691, "rate_01": -10, "svolume_01": -705,
        "jjcode_03": "", "ms_03": 432, "md_03": 462, "rate_03": -1, "svolume_03": -30,
        "jjcode_04": "", "ms_04": 3, "md_04": 2, "rate_04": 0, "svolume_04": 1,
        "jjcode_02": "", "ms_02": 52, "md_02": 53, "rate_02": 0, "svolume_02": -1,
        "jjcode_05": "", "ms_05": 8, "md_05": 58, "rate_05": -2, "svolume_05": -51,
        "jjcode_06": "", "ms_06": 151, "md_06": 123, "rate_06": -2, "svolume_06": 27,
        "jjcode_11": "", "ms_11": 0, "md_11": 0, "rate_11": 34, "svolume_11": 0,
        "jjcode_07": "", "ms_07": 1605, "md_07": 3908, "rate_07": -24, "svolume_07": -2304,
        "jjcode_00": "", "ms_00": 451, "md_00": 753, "rate_00": -2, "svolume_00": -302
      }
    }
    """;

    const string DailyThreeRowSample = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "OK",
      "t1702OutBlock1": [
        {
          "date": "20250805", "close": 3280, "sign": "2", "change": 60, "diff": "1.86",
          "volume": 887335, "value": 2922,
          "tjj0000": -1, "tjj0001": 83, "tjj0002": 0, "tjj0003": 0, "tjj0004": 0,
          "tjj0005": 0, "tjj0006": 89, "tjj0007": -4, "tjj0008": -554,
          "tjj0009": 385, "tjj0010": 1, "tjj0011": 0,
          "tjj0018": 171, "tjj0016": 387, "tjj0017": -4
        },
        {
          "date": "20250804", "close": 3220, "sign": "2", "change": 65, "diff": "2.06",
          "volume": 814070, "value": 2603,
          "tjj0000": -158, "tjj0001": -18, "tjj0002": 0, "tjj0003": 0, "tjj0004": 0,
          "tjj0005": 0, "tjj0006": -10, "tjj0007": 24, "tjj0008": -68,
          "tjj0009": 232, "tjj0010": -2, "tjj0011": 0,
          "tjj0018": -186, "tjj0016": 230, "tjj0017": 24
        },
        {
          "date": "20250801", "close": 3155, "sign": "5", "change": -225, "diff": "-6.66",
          "volume": 1810509, "value": 5815,
          "tjj0000": 0, "tjj0001": -140, "tjj0002": 0, "tjj0003": 0, "tjj0004": 0,
          "tjj0005": 0, "tjj0006": 0, "tjj0007": 20, "tjj0008": -1023,
          "tjj0009": 1143, "tjj0010": -1, "tjj0011": 0,
          "tjj0018": -140, "tjj0016": 1143, "tjj0017": 20
        }
      ]
    }
    """;

    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    [Fact]
    public async Task GetInvestorFlow_NoShcode_CallsT1601_DefaultsVolumeUnit()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(IntradayTwoSegmentSample));

        string result = await GetInvestorFlowTool.GetInvestorFlow(client);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1601");
        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        // Default unit=volume → gubun1/2/4 all "1"; exchgubun=U.
        body.Should().Contain("\"gubun1\":\"1\"");
        body.Should().Contain("\"gubun2\":\"1\"");
        body.Should().Contain("\"gubun4\":\"1\"");
        body.Should().Contain("\"exchgubun\":\"U\"");

        root.GetProperty("mode").GetString().Should().Be("intraday");
        root.GetProperty("unit").GetString().Should().Be("volume");
        root.GetProperty("exchange").GetString().Should().Be("unified");
        JsonElement segments = root.GetProperty("segments");
        segments.GetArrayLength().Should().Be(2, "OutBlocks 3..6 are absent in the sample so only two segments surface");
        segments[0].GetProperty("block_index").GetInt32().Should().Be(1);
        JsonElement firstInvestor = segments[0].GetProperty("investors")[0];
        firstInvestor.GetProperty("kind").GetString().Should().Be("individual");
        firstInvestor.GetProperty("korean_label").GetString().Should().Be("개인");
        firstInvestor.GetProperty("net").GetInt64().Should().Be(-8398);
        firstInvestor.GetProperty("buy").GetInt64().Should().Be(205539);
        firstInvestor.GetProperty("sell").GetInt64().Should().Be(213937);
    }

    [Fact]
    public async Task GetInvestorFlow_NoShcode_ValueUnit_MapsGubunTwo()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(IntradayTwoSegmentSample));

        await GetInvestorFlowTool.GetInvestorFlow(client, unit: "value");

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"gubun1\":\"2\"");
        body.Should().Contain("\"gubun2\":\"2\"");
        body.Should().Contain("\"gubun4\":\"2\"");
    }

    [Fact]
    public async Task GetInvestorFlow_WithShcode_CallsT1702_WithDefaultDailyArgs()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(DailyThreeRowSample));

        string result = await GetInvestorFlowTool.GetInvestorFlow(client, shcode: "001200");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1702");
        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"shcode\":\"001200\"");
        body.Should().Contain("\"volvalgb\":\"1\"", "default metric=volume → '1'");
        body.Should().Contain("\"msmdgb\":\"0\"", "default direction=net → '0'");
        body.Should().Contain("\"gubun\":\"0\"", "default cumulative=false → '0'");
        body.Should().Contain("\"exchgubun\":\"U\"");

        root.GetProperty("mode").GetString().Should().Be("daily");
        root.GetProperty("shcode").GetString().Should().Be("001200");
        root.GetProperty("metric").GetString().Should().Be("volume");
        root.GetProperty("direction").GetString().Should().Be("net");
        root.GetProperty("cumulative").GetBoolean().Should().BeFalse();
        JsonElement series = root.GetProperty("time_series");
        series.GetArrayLength().Should().Be(3);
        JsonElement first = series[0];
        first.GetProperty("date").GetString().Should().Be("20250805");
        first.GetProperty("close").GetDouble().Should().Be(3280);
        first.GetProperty("change_pct").GetDouble().Should().BeApproximately(1.86, 1e-2);
        JsonElement flows = first.GetProperty("flows");
        flows.GetArrayLength().Should().Be(12);
        // Individual (개인) maps to tjj0008.
        JsonElement individualFlow = flows.EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "individual");
        individualFlow.GetProperty("value").GetInt64().Should().Be(-554);
        // Foreign (외국인) maps to tjj0016 combined registered+unregistered.
        JsonElement foreignFlow = flows.EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "foreign");
        foreignFlow.GetProperty("value").GetInt64().Should().Be(387);
    }

    [Theory]
    [InlineData("net", "0")]
    [InlineData("buy", "1")]
    [InlineData("sell", "2")]
    public async Task GetInvestorFlow_DirectionMapping(string input, string expected)
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(DailyThreeRowSample));

        await GetInvestorFlowTool.GetInvestorFlow(client, shcode: "005930", direction: input);

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain($"\"msmdgb\":\"{expected}\"");
    }

    [Theory]
    [InlineData("value", "0")]
    [InlineData("volume", "1")]
    [InlineData("price", "2")]
    public async Task GetInvestorFlow_MetricMapping(string input, string expected)
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(DailyThreeRowSample));

        await GetInvestorFlowTool.GetInvestorFlow(client, shcode: "005930", metric: input);

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain($"\"volvalgb\":\"{expected}\"");
    }

    [Fact]
    public async Task GetInvestorFlow_Cumulative_MapsGubunOne()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(DailyThreeRowSample));

        await GetInvestorFlowTool.GetInvestorFlow(client, shcode: "005930", cumulative: true);

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"gubun\":\"1\"");
    }

    [Fact]
    public async Task GetInvestorFlow_ExplicitDateRange_PassesThrough()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(DailyThreeRowSample));

        await GetInvestorFlowTool.GetInvestorFlow(client, shcode: "005930", fromdt: "20250101", todt: "20250131");

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"fromdt\":\"20250101\"");
        body.Should().Contain("\"todt\":\"20250131\"");
    }

    [Fact]
    public async Task GetInvestorFlow_FromAfterTo_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DailyThreeRowSample));

        string result = await GetInvestorFlowTool.GetInvestorFlow(client, shcode: "005930", fromdt: "20250201", todt: "20250101");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("fromdt must be <= todt");
    }

    [Fact]
    public async Task GetInvestorFlow_CountClipsResultSize()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DailyThreeRowSample));

        string result = await GetInvestorFlowTool.GetInvestorFlow(client, shcode: "005930", count: 2);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("time_series").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task GetInvestorFlow_BusinessError_SurfacesEnvelope()
    {
        const string body = """{"rsp_cd":"99999","rsp_msg":"필수항목 누락"}""";
        var (client, _) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetInvestorFlowTool.GetInvestorFlow(client, shcode: "005930");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("business-level");
        root.GetProperty("details").GetProperty("rsp_cd").GetString().Should().Be("99999");
        root.GetProperty("details").GetProperty("shcode").GetString().Should().Be("005930");
    }

    [Fact]
    public async Task GetInvestorFlow_UnknownExchange_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(IntradayTwoSegmentSample));

        string result = await GetInvestorFlowTool.GetInvestorFlow(client, exchange: "nasdaq");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("exchange");
    }

    [Fact]
    public async Task GetInvestorFlow_UnknownUnit_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(IntradayTwoSegmentSample));

        string result = await GetInvestorFlowTool.GetInvestorFlow(client, unit: "rubbish");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("unit");
    }

    [Fact]
    public async Task GetInvestorFlow_CountOutOfRange_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DailyThreeRowSample));

        string result = await GetInvestorFlowTool.GetInvestorFlow(client, shcode: "005930", count: 0);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("count");
    }
}
