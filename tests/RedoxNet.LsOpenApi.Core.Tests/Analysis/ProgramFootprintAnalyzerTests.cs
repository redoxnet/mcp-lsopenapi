using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Analysis;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Analysis;

public class ProgramFootprintAnalyzerTests
{
    static ProgramFlowDay Day(int i, double net, double grossBuy, double grossSell) =>
        new($"2026-05-{i + 1:D2}", net, grossBuy, grossSell);

    /// <summary>10 days, 8 net-buy (one-directional — gross dominated by the buy side).</summary>
    static List<ProgramFlowDay> AccumulationDays()
    {
        var d = new List<ProgramFlowDay>();
        for (int i = 0; i < 10; i++)
        {
            bool sell = i is 1 or 4;   // two sell days early; recent run is all buys
            d.Add(sell ? Day(i, -60, 30, 90) : Day(i, 120, 150, 30));
        }
        return d;
    }

    /// <summary>10 days of heavy two-way flow with a tiny alternating net.</summary>
    static List<ProgramFlowDay> ChurnDays()
    {
        var d = new List<ProgramFlowDay>();
        for (int i = 0; i < 10; i++)
        {
            double net = i % 2 == 0 ? 12 : -10;
            d.Add(new($"2026-05-{i + 1:D2}", net, 600 + net, 600));
        }
        return d;
    }

    /// <summary>40 minutes of steady, one-directional accumulation tracking a falling price.</summary>
    static List<ProgramFlowMinute> SteadyIntraday()
    {
        var m = new List<ProgramFlowMinute>();
        for (int i = 0; i < 40; i++)
            m.Add(new($"09:{i:D2}", -10.0 * (i + 1), 70_000 - 10 * i));
        return m;
    }

    [Fact]
    public void Analyze_OneDirectionalBuying_IsAccumulation()
    {
        ProgramFootprintReport r = ProgramFootprintAnalyzer.Analyze(
            AccumulationDays(), Array.Empty<ProgramFlowMinute>());

        r.Regime.Should().Be("accumulation");
        r.DirectionConfidence.Should().BeGreaterThan(0);
        r.Signals.BuyDays.Should().Be(8);
        r.Signals.WindowNet.Should().BeGreaterThan(0);
        r.Signals.ChurnRatio.Should().BeGreaterThan(0.15, "one-directional flow is not churn");
        r.Evidence.Should().NotBeEmpty();
    }

    [Fact]
    public void Analyze_HeavyTwoWayFlow_IsChurn()
    {
        ProgramFootprintReport r = ProgramFootprintAnalyzer.Analyze(
            ChurnDays(), Array.Empty<ProgramFlowMinute>());

        r.Regime.Should().Be("churn");
        r.Signals.ChurnRatio.Should().BeLessThan(0.15);
        r.Signals.BuyDays.Should().BeGreaterThanOrEqualTo(3);
        r.Signals.SellDays.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Analyze_MirroredSelling_IsDistribution()
    {
        // Flip the accumulation fixture's sign → a sustained sell regime.
        List<ProgramFlowDay> sell = AccumulationDays()
            .Select(d => new ProgramFlowDay(d.Date, -d.Net, d.GrossSell, d.GrossBuy))
            .ToList();

        ProgramFootprintReport r = ProgramFootprintAnalyzer.Analyze(
            sell, Array.Empty<ProgramFlowMinute>());

        r.Regime.Should().Be("distribution");
        r.Signals.WindowNet.Should().BeLessThan(0);
    }

    [Fact]
    public void Analyze_WithoutIntraday_ReportsIntradaySignalsAsNotAvailable()
    {
        ProgramFootprintReport r = ProgramFootprintAnalyzer.Analyze(
            AccumulationDays(), Array.Empty<ProgramFlowMinute>());

        r.Signals.PaceRegularity.Should().Be("n/a");
        r.Signals.Loading.Should().Be("n/a");
        r.Signals.PriceCoupling.Should().Be(0);
    }

    [Fact]
    public void Analyze_SteadyIntraday_DetectsSteadyPaceAndCoupling()
    {
        ProgramFootprintReport r = ProgramFootprintAnalyzer.Analyze(
            AccumulationDays(), SteadyIntraday());

        r.Signals.PaceRegularity.Should().Be("steady");
        r.Signals.PaceCv.Should().BeLessThan(1.0);
        // Cumulative net and price both fall in lockstep → strong positive correlation.
        r.Signals.PriceCoupling.Should().BeGreaterThan(0.9);
    }

    [Fact]
    public void Analyze_EmptyDaily_Throws()
    {
        Action act = () => ProgramFootprintAnalyzer.Analyze(
            Array.Empty<ProgramFlowDay>(), Array.Empty<ProgramFlowMinute>());

        act.Should().Throw<ArgumentException>();
    }
}
