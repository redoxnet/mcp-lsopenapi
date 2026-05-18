using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public sealed class GetStockEventsToolTests
{
    // Mixed sample: one TBD entry (recdt='00000000') + a dated 주주총회 + a dated 배당,
    // covering the date-tbd path, the sort order, and kind-filtering.
    const string MixedSample = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "OK",
      "t3202OutBlock": [
        {
          "recdt": "00000000",
          "tableid": "SA02BS",
          "upgu": "09",
          "custno": "00120",
          "custnm": "유진투자증권(주)",
          "shcode": "001200",
          "upunm": "주주총회"
        },
        {
          "recdt": "20260328",
          "tableid": "SA02BS",
          "upgu": "09",
          "custno": "00120",
          "custnm": "유진투자증권(주)",
          "shcode": "001200",
          "upunm": "정기주주총회"
        },
        {
          "recdt": "20251231",
          "tableid": "SA02BS",
          "upgu": "03",
          "custno": "00120",
          "custnm": "유진투자증권(주)",
          "shcode": "001200",
          "upunm": "현금배당"
        }
      ]
    }
    """;

    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    [Fact]
    public async Task GetStockEvents_NoFilters_ReturnsAllEventsSortedByDateThenTbd()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(MixedSample));

        string result = await GetStockEventsTool.GetStockEvents(client, shcode: "001200");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t3202");
        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"shcode\":\"001200\"");

        root.GetProperty("shcode").GetString().Should().Be("001200");
        root.GetProperty("count").GetInt32().Should().Be(3);
        JsonElement events = root.GetProperty("events");
        // Sorted: dated ascending first, TBD last.
        events[0].GetProperty("date").GetString().Should().Be("20251231");
        events[0].GetProperty("kind").GetString().Should().Be("dividend");
        events[0].GetProperty("upgu_code").GetString().Should().Be("03");
        events[0].GetProperty("date_tbd").GetBoolean().Should().BeFalse();
        events[1].GetProperty("date").GetString().Should().Be("20260328");
        events[1].GetProperty("kind").GetString().Should().Be("shareholder_meeting");
        events[2].GetProperty("date_tbd").GetBoolean().Should().BeTrue();
        events[2].TryGetProperty("date", out _).Should().BeFalse("TBD entries omit the date key");
    }

    [Fact]
    public async Task GetStockEvents_FromToFilter_KeepsTbdAndClipsDated()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(MixedSample));

        // [2026-01-01, 2026-12-31] keeps the 정기주주총회 and the TBD entry; drops the 2025-12-31 dividend.
        string result = await GetStockEventsTool.GetStockEvents(client, shcode: "001200", from: "20260101", to: "20261231");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("count").GetInt32().Should().Be(2);
        JsonElement events = root.GetProperty("events");
        events[0].GetProperty("date").GetString().Should().Be("20260328");
        events[1].GetProperty("date_tbd").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("dividend")]
    [InlineData("배당")]
    [InlineData("03")]
    public async Task GetStockEvents_KindAliases_AllResolveToSameUpgu(string alias)
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(MixedSample));

        string result = await GetStockEventsTool.GetStockEvents(client, shcode: "001200", kinds: new[] { alias });
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("count").GetInt32().Should().Be(1);
        root.GetProperty("events")[0].GetProperty("upgu_code").GetString().Should().Be("03");
    }

    [Fact]
    public async Task GetStockEvents_MultiKindFilter_KeepsAllMatches()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(MixedSample));

        string result = await GetStockEventsTool.GetStockEvents(client, shcode: "001200", kinds: new[] { "dividend", "agm" });
        JsonElement root = JsonDocument.Parse(result).RootElement;

        // dividend (03) + shareholder_meeting (09) — keeps all 3 (TBD AGM + dated AGM + dated dividend).
        root.GetProperty("count").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task GetStockEvents_EmptyShcode_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(MixedSample));

        string result = await GetStockEventsTool.GetStockEvents(client, shcode: "");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("shcode");
    }

    [Fact]
    public async Task GetStockEvents_UnknownKind_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(MixedSample));

        string result = await GetStockEventsTool.GetStockEvents(client, shcode: "005930", kinds: new[] { "nonsense_event" });
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("not recognized");
    }

    [Fact]
    public async Task GetStockEvents_BadDate_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(MixedSample));

        string result = await GetStockEventsTool.GetStockEvents(client, shcode: "005930", from: "2026-01-01");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("YYYYMMDD");
    }

    [Fact]
    public async Task GetStockEvents_FromAfterTo_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(MixedSample));

        string result = await GetStockEventsTool.GetStockEvents(client, shcode: "005930", from: "20260301", to: "20260101");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("from must be <= to");
    }

    [Fact]
    public async Task GetStockEvents_BusinessError_SurfacesEnvelope()
    {
        const string body = """{"rsp_cd":"99999","rsp_msg":"필수항목 누락"}""";
        var (client, _) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetStockEventsTool.GetStockEvents(client, shcode: "005930");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("business-level");
        root.GetProperty("details").GetProperty("rsp_cd").GetString().Should().Be("99999");
        root.GetProperty("details").GetProperty("shcode").GetString().Should().Be("005930");
    }
}
