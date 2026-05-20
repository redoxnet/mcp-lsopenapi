using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Covers the v0.10 <c>ls_get_index_history</c> dataset export + drill
/// (SPEC-v0.10 §5.3, pattern C): output_mode="export" caches the whole series
/// behind a dataset_id and returns only the digest; a follow-up call with that
/// dataset_id slices the cached bars with no further API call. Pinned to the
/// cache collection — the export handle lives in the process-wide
/// <c>DatasetHandleCache</c>.
/// </summary>
[Collection(ChartDatasetCacheCollection.Name)]
public sealed class GetIndexHistoryToolExportTests
{
    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    });

    /// <summary>
    /// Builds a synthetic t1514 response with <paramref name="bars"/> rows,
    /// newest first, the newest bar dated 2026-05-18 and one calendar day apart.
    /// </summary>
    static string BuildSample(int bars)
    {
        var sb = new StringBuilder();
        sb.Append("""{"rsp_cd":"00000","rsp_msg":"OK","t1514OutBlock":{"cts_date":"        "},"t1514OutBlock1":[""");
        for (int i = 0; i < bars; i++)
        {
            if (i > 0) sb.Append(',');
            string date = new DateTime(2026, 5, 18).AddDays(-i).ToString("yyyyMMdd");
            int jisu = 2600 + i;
            sb.Append('{');
            sb.Append($"\"date\":\"{date}\",\"jisu\":\"{jisu}.00\",\"sign\":\"2\",\"change\":\"5.00\",\"diff\":\"0.20\",");
            sb.Append("\"volume\":123456,\"diff_vol\":\"10\",\"value1\":7890123,\"value2\":7890123,");
            sb.Append("\"high\":500,\"unchg\":80,\"low\":420,\"up\":1,\"down\":0,\"totjo\":1000,\"uprate\":\"50\",");
            sb.Append($"\"openjisu\":\"{jisu - 1}.00\",\"highjisu\":\"{jisu + 3}.00\",\"lowjisu\":\"{jisu - 4}.00\",");
            sb.Append("\"frgsvolume\":100,\"orgsvolume\":-50,\"upcode\":\"001\",\"rate\":\"0\",\"divrate\":\"0\"");
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    [Fact]
    public async Task Export_ReturnsDatasetIdAndDigest_NoPoints()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(BuildSample(60)));

        string result = await GetIndexHistoryTool.GetIndexHistory(
            client, "kospi", count: 60, output_mode: "export");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("output_mode").GetString().Should().Be("export");
        root.GetProperty("dataset_id").GetString().Should().StartWith("ds_");
        root.GetProperty("count").GetInt32().Should().Be(60);
        root.TryGetProperty("summary", out _).Should().BeTrue("export still ships the aggregate digest");
        root.TryGetProperty("points", out _).Should().BeFalse("export keeps per-bar points out of context");
        root.GetProperty("drill_hint").GetString().Should().Contain("dataset_id");
    }

    [Fact]
    public async Task Export_CountAboveExportMax_ReturnsValidationError()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(BuildSample(1)));

        string result = await GetIndexHistoryTool.GetIndexHistory(
            client, "kospi", count: 2501, output_mode: "export");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().BeEmpty("count validation short-circuits before any TR call");
        root.GetProperty("error").GetString().Should().Contain("count");
    }

    [Fact]
    public async Task Export_AllowsCountAboveSummaryCap()
    {
        // 600 bars exceeds the summary cap (500) but is valid for export.
        var (client, _) = TestClientFactory.Create((_, _) => Ok(BuildSample(600)));

        string result = await GetIndexHistoryTool.GetIndexHistory(
            client, "kospi", count: 600, output_mode: "export");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("error", out _).Should().BeFalse();
        root.GetProperty("count").GetInt32().Should().Be(600);
    }

    [Fact]
    public async Task ExportThenDrill_RoundTripsAllBars_WithNoSecondApiCall()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(BuildSample(60)));

        string exported = await GetIndexHistoryTool.GetIndexHistory(
            client, "kospi", count: 60, output_mode: "export");
        string datasetId = JsonDocument.Parse(exported).RootElement.GetProperty("dataset_id").GetString()!;

        string drilled = await GetIndexHistoryTool.GetIndexHistory(client, dataset_id: datasetId);
        JsonElement root = JsonDocument.Parse(drilled).RootElement;

        handler.Requests.Should().ContainSingle("the drill slices the cached series — no second TR call");
        root.GetProperty("dataset_id").GetString().Should().Be(datasetId);
        root.GetProperty("count").GetInt32().Should().Be(60);
        JsonElement points = root.GetProperty("points");
        points.GetArrayLength().Should().Be(60);
        // Cached bars are stored ascending by date.
        points[0].GetProperty("date").GetString().Should().Be("20260320");
        points[59].GetProperty("date").GetString().Should().Be("20260518");
        root.TryGetProperty("summary", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Drill_RecentN_KeepsMostRecentBars()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(BuildSample(60)));
        string exported = await GetIndexHistoryTool.GetIndexHistory(
            client, "kospi", count: 60, output_mode: "export");
        string datasetId = JsonDocument.Parse(exported).RootElement.GetProperty("dataset_id").GetString()!;

        string drilled = await GetIndexHistoryTool.GetIndexHistory(
            client, dataset_id: datasetId, recent_n: 10);
        JsonElement root = JsonDocument.Parse(drilled).RootElement;

        root.GetProperty("recent_n").GetInt32().Should().Be(10);
        JsonElement points = root.GetProperty("points");
        points.GetArrayLength().Should().Be(10);
        points[0].GetProperty("date").GetString().Should().Be("20260509");
        points[9].GetProperty("date").GetString().Should().Be("20260518");
    }

    [Fact]
    public async Task Drill_FromTo_FiltersByInclusiveDateRange()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(BuildSample(60)));
        string exported = await GetIndexHistoryTool.GetIndexHistory(
            client, "kospi", count: 60, output_mode: "export");
        string datasetId = JsonDocument.Parse(exported).RootElement.GetProperty("dataset_id").GetString()!;

        string drilled = await GetIndexHistoryTool.GetIndexHistory(
            client, dataset_id: datasetId, from: "20260501", to: "20260510");
        JsonElement root = JsonDocument.Parse(drilled).RootElement;

        JsonElement points = root.GetProperty("points");
        points.GetArrayLength().Should().Be(10, "20260501..20260510 inclusive is 10 calendar days");
        points[0].GetProperty("date").GetString().Should().Be("20260501");
        points[9].GetProperty("date").GetString().Should().Be("20260510");
    }

    [Fact]
    public async Task Drill_UnknownDatasetId_ReturnsError()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(BuildSample(1)));

        string result = await GetIndexHistoryTool.GetIndexHistory(
            client, dataset_id: "ds_not_a_real_handle");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().BeEmpty("a drill never calls LS");
        root.GetProperty("error").GetString().Should().Contain("dataset_id");
    }
}
