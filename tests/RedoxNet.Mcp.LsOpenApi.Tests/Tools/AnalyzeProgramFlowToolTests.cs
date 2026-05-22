using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using RedoxNet.LsOpenApi.Core.Http;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Tool-level tests for <see cref="AnalyzeProgramFlowTool"/> — the Analysis-Layer
/// footprint classifier. The stub routes the tool's two t1637 calls (intraday
/// gubun2=0, daily gubun2=1) to the matching fixture body.
/// </summary>
[Collection(ChartDatasetCacheCollection.Name)]
public class AnalyzeProgramFlowToolTests
{
    /// <summary>Builds a client whose two t1637 calls resolve by the request's gubun2.</summary>
    static (LsApiClient Client, StubHttpMessageHandler Handler) Make(
        string intradayBody, string dailyBody)
        => TestClientFactory.Create(async (req, ct) =>
        {
            string body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(body);
            string gubun2 = doc.RootElement.GetProperty("t1637InBlock").GetProperty("gubun2").GetString()!;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    gubun2 == "0" ? intradayBody : dailyBody, Encoding.UTF8, "application/json"),
            };
        });

    static JsonElement ParseTextContent(CallToolResult result)
    {
        TextContentBlock text = (TextContentBlock)result.Content[0];
        return JsonDocument.Parse(text.Text).RootElement;
    }

    /// <summary>One t1637 daily row — net-buy, one-directional gross (천원).</summary>
    static string DailyRow(string date, long net, long grossBuy, long grossSell) =>
        $$"""{"date":"{{date}}","time":"","price":300000,"svalue":{{net}},"offervalue":{{grossSell}},"stksvalue":{{grossBuy}},"svolume":0,"offervolume":0,"stksvolume":0,"diff":"1.00","sign":"2","change":1000,"volume":50000,"shcode":"005930","ex_shcode":"005930"}""";

    /// <summary>One t1637 intraday row — cumulative net only (천원).</summary>
    static string IntradayRow(string time, long price, long svalue) =>
        $$"""{"date":"20260522","time":"{{time}}","price":{{price}},"svalue":{{svalue}},"offervalue":0,"stksvalue":0,"svolume":0,"offervolume":0,"stksvolume":0,"diff":"0","sign":"","change":0,"volume":0,"shcode":"005930","ex_shcode":"005930"}""";

    /// <summary>20 newest-first daily rows, every day a one-directional net buy.</summary>
    static string T1637DailyBody()
    {
        var sb = new StringBuilder();
        var d = new DateTime(2026, 5, 22);
        for (int i = 0; i < 20; i++)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(DailyRow(d.ToString("yyyyMMdd"), 10_000_000, 12_000_000, 2_000_000));
            d = d.AddDays(-1);
        }
        return $$"""{"t1637OutBlock":{"cts_idx":0},"rsp_cd":"00000","rsp_msg":"OK","t1637OutBlock1":[{{sb}}]}""";
    }

    /// <summary>10 newest-first intraday rows with a steadily rising cumulative net.</summary>
    static string T1637IntradayBody()
    {
        var sb = new StringBuilder();
        for (int min = 10; min >= 1; min--)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(IntradayRow($"09{min:D2}00", 300_000 + min * 100, min * 1_000_000L));
        }
        return $$"""{"t1637OutBlock":{"cts_idx":0},"rsp_cd":"00000","rsp_msg":"OK","t1637OutBlock1":[{{sb}}]}""";
    }

    /// <summary>An empty intraday response — the market has not opened.</summary>
    const string EmptyIntradayBody =
        """{"t1637OutBlock":{"cts_idx":0},"rsp_cd":"00000","rsp_msg":"OK","t1637OutBlock1":[]}""";

    [Fact]
    public async Task AnalyzeProgramFlow_OneDirectionalBuying_ClassifiesAccumulation()
    {
        var (client, _) = Make(T1637IntradayBody(), T1637DailyBody());

        CallToolResult result = await AnalyzeProgramFlowTool.AnalyzeProgramFlow(
            client, "005930", name: "삼성전자");

        result.IsError.Should().NotBe(true);
        JsonElement text = ParseTextContent(result);
        text.GetProperty("tool").GetString().Should().Be("analyze_program_flow");
        text.GetProperty("regime").GetString().Should().Be("accumulation");
        text.GetProperty("direction_confidence").GetDouble().Should().BeGreaterThan(0);
        text.GetProperty("signals").GetProperty("buy_days").GetInt32().Should().Be(20);
        text.GetProperty("evidence").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AnalyzeProgramFlow_NoIntraday_StillAnalyzesFromDaily()
    {
        var (client, _) = Make(EmptyIntradayBody, T1637DailyBody());

        CallToolResult result = await AnalyzeProgramFlowTool.AnalyzeProgramFlow(
            client, "005930");

        JsonElement text = ParseTextContent(result);
        text.GetProperty("regime").GetString().Should().Be("accumulation");
        text.GetProperty("signals").GetProperty("pace_regularity").GetString().Should().Be("n/a");
    }

    [Fact]
    public async Task AnalyzeProgramFlow_MissingShcode_ReturnsError()
    {
        var (client, handler) = Make(T1637IntradayBody(), T1637DailyBody());

        CallToolResult result = await AnalyzeProgramFlowTool.AnalyzeProgramFlow(client, "");

        result.IsError.Should().BeTrue();
        handler.Requests.Should().BeEmpty("a missing shcode must fail before any TR call");
    }
}
