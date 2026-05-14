using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Charting;
using Xunit;
using View = RedoxNet.LsOpenApi.Core.Charting.EtfHoldingsChartBuilder.EtfHoldingView;

namespace RedoxNet.LsOpenApi.Core.Tests.Charting;

/// <summary>
/// Direct unit tests for <see cref="EtfHoldingsChartBuilder"/> covering the
/// design contract: top-10 label suppression, concentration buckets,
/// single-name and cash notes, and treemap shape.
/// </summary>
public class EtfHoldingsChartBuilderTests
{
    /// <summary>Generates N synthetic holdings whose weights are spaced
    /// evenly under <paramref name="totalWeight"/>; rank 0 is largest.</summary>
    static List<View> SyntheticHoldings(int count, double totalWeight = 100.0)
    {
        // Use a simple decreasing series: w_i = (count - i) / sum.
        double sum = count * (count + 1) / 2.0;
        var list = new List<View>(count);
        for (int i = 0; i < count; i++)
        {
            double w = (count - i) / sum * totalWeight;
            list.Add(new View($"00{i:D4}", $"종목{i + 1}", w, 1_000_000 - i * 10_000));
        }
        return list;
    }

    [Fact]
    public void Build_EmptyHoldings_ReturnsNull()
    {
        JsonObject? result = EtfHoldingsChartBuilder.Build("069500", Array.Empty<View>(), cashPercent: null);
        result.Should().BeNull();
    }

    [Fact]
    public void Build_PlotlyEnvelope_HasTreemapTrace()
    {
        JsonObject? result = EtfHoldingsChartBuilder.Build("069500", SyntheticHoldings(5), cashPercent: null);

        result.Should().NotBeNull();
        JsonElement chart = JsonSerializer.SerializeToElement(result!["chart"]);
        chart.GetProperty("type").GetString().Should().Be("plotly");
        chart.GetProperty("version").GetString().Should().Be("5");

        JsonElement trace = chart.GetProperty("spec").GetProperty("data")[0];
        trace.GetProperty("type").GetString().Should().Be("treemap");
        trace.GetProperty("labels").GetArrayLength().Should().Be(5);
        trace.GetProperty("values").GetArrayLength().Should().Be(5);
        trace.GetProperty("parents").GetArrayLength().Should().Be(5);
        // Flat tree: every parent is empty string.
        foreach (JsonElement p in trace.GetProperty("parents").EnumerateArray())
            p.GetString().Should().BeEmpty();
    }

    [Fact]
    public void Build_VisibleLabels_Top10OnlyRestEmpty()
    {
        JsonObject? result = EtfHoldingsChartBuilder.Build("069500", SyntheticHoldings(15), cashPercent: null);

        JsonElement text = JsonSerializer.SerializeToElement(result!["chart"])
            .GetProperty("spec").GetProperty("data")[0].GetProperty("text");

        // Indices 0..9: labelled ("종목N<br>X.XX%"). Indices 10..14: empty.
        for (int i = 0; i < 10; i++)
            text[i].GetString().Should().NotBeEmpty("rank {0} is in the top 10", i);
        for (int i = 10; i < 15; i++)
            text[i].GetString().Should().BeEmpty("rank {0} should suppress its in-cell label", i);
    }

    [Theory]
    // Top-5 cumulative weight → expected badge label. Cutoffs: 35 / 60 / 80.
    [InlineData(new[] { 5.0, 5.0, 5.0, 5.0, 5.0 }, "분산형")]       // 25%
    [InlineData(new[] { 10.0, 9.0, 8.0, 7.0, 6.0 }, "균형형")]       // 40%
    [InlineData(new[] { 15.0, 12.0, 10.0, 8.0, 5.0 }, "균형형")]     // 50%
    [InlineData(new[] { 20.0, 18.0, 15.0, 12.0, 10.0 }, "집중형")]   // 75%
    [InlineData(new[] { 30.0, 25.0, 15.0, 10.0, 5.0 }, "초집중형")]  // 85%
    public void Build_ConcentrationBadge_Buckets(double[] weights, string expectedBadge)
    {
        var holdings = weights.Select((w, i) =>
            new View($"00{i:D4}", $"종목{i + 1}", w, 1_000)).ToList();

        JsonObject? result = EtfHoldingsChartBuilder.Build("069500", holdings, cashPercent: null);
        string badge = JsonSerializer.SerializeToElement(result!["panel"])
            .GetProperty("concentration").GetProperty("badge").GetString()!;

        badge.Should().Be(expectedBadge);
    }

    [Fact]
    public void Build_SingleHeavyweight_AddsThirtyPlusNote()
    {
        var holdings = new List<View>
        {
            new("373220", "LG에너지솔루션", 32.5, 5_000_000),
            new("005930", "삼성전자",       20.0, 3_000_000),
            new("000660", "SK하이닉스",     15.0, 2_000_000),
        };
        JsonObject? result = EtfHoldingsChartBuilder.Build("305720", holdings, cashPercent: null);
        JsonElement notes = JsonSerializer.SerializeToElement(result!["panel"]).GetProperty("notes");

        notes.GetArrayLength().Should().BeGreaterThan(0);
        bool hasHeavyweightNote = false;
        foreach (JsonElement n in notes.EnumerateArray())
            if (n.GetString()!.Contains("LG에너지솔루션") && n.GetString()!.Contains("30%"))
                hasHeavyweightNote = true;
        hasHeavyweightNote.Should().BeTrue();
    }

    [Fact]
    public void Build_CashAboveThreshold_AddsCashNote()
    {
        JsonObject? result = EtfHoldingsChartBuilder.Build("069500", SyntheticHoldings(5), cashPercent: 3.4);
        JsonElement notes = JsonSerializer.SerializeToElement(result!["panel"]).GetProperty("notes");

        bool hasCashNote = false;
        foreach (JsonElement n in notes.EnumerateArray())
            if (n.GetString()!.Contains("현금")) hasCashNote = true;
        hasCashNote.Should().BeTrue();
    }

    [Fact]
    public void Build_CashBelowFloor_DoesNotAddCashNote()
    {
        // 0.04% — below the 0.05% noise floor.
        JsonObject? result = EtfHoldingsChartBuilder.Build("069500", SyntheticHoldings(5), cashPercent: 0.04);
        JsonElement notes = JsonSerializer.SerializeToElement(result!["panel"]).GetProperty("notes");

        foreach (JsonElement n in notes.EnumerateArray())
            n.GetString().Should().NotContain("현금");
    }

    [Fact]
    public void Build_TopHoldings_CumulativeIsMonotonicallyIncreasing()
    {
        JsonObject? result = EtfHoldingsChartBuilder.Build("069500", SyntheticHoldings(8), cashPercent: null);
        JsonElement top = JsonSerializer.SerializeToElement(result!["panel"]).GetProperty("top_holdings");

        double prev = 0;
        foreach (JsonElement row in top.EnumerateArray())
        {
            double cum = row.GetProperty("cumulative_pct").GetDouble();
            cum.Should().BeGreaterThanOrEqualTo(prev);
            prev = cum;
        }
    }

    [Fact]
    public void Build_TopHoldings_LimitedToTenEvenWhenMoreSupplied()
    {
        JsonObject? result = EtfHoldingsChartBuilder.Build("069500", SyntheticHoldings(25), cashPercent: null);
        JsonElement top = JsonSerializer.SerializeToElement(result!["panel"]).GetProperty("top_holdings");
        top.GetArrayLength().Should().Be(10);
    }

    [Fact]
    public void Build_SortsByWeightDescending_EvenIfInputIsOutOfOrder()
    {
        var shuffled = new List<View>
        {
            new("000003", "C", 10.0, 0),
            new("000001", "A", 50.0, 0),
            new("000002", "B", 30.0, 0),
        };
        JsonObject? result = EtfHoldingsChartBuilder.Build("069500", shuffled, cashPercent: null);
        JsonElement top = JsonSerializer.SerializeToElement(result!["panel"]).GetProperty("top_holdings");

        top[0].GetProperty("name").GetString().Should().Be("A");
        top[1].GetProperty("name").GetString().Should().Be("B");
        top[2].GetProperty("name").GetString().Should().Be("C");
    }
}
