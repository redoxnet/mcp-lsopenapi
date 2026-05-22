namespace RedoxNet.LsOpenApi.Core.Analysis;

/// <summary>One day of a stock's program-trading flow — analyzer input, amounts in 억원.</summary>
/// <param name="Date">Session date, <c>yyyy-MM-dd</c>.</param>
/// <param name="Net">That day's program net buying (= buy − sell).</param>
/// <param name="GrossBuy">That day's program gross buying.</param>
/// <param name="GrossSell">That day's program gross selling.</param>
internal sealed record ProgramFlowDay(string Date, double Net, double GrossBuy, double GrossSell);

/// <summary>One minute of a stock's intraday program-trading flow — analyzer input, 억원.</summary>
/// <param name="Time">Display time, <c>HH:mm</c>.</param>
/// <param name="CumulativeNet">Program net buying, cumulative from the session open.</param>
/// <param name="Price">Stock price at the minute.</param>
internal sealed record ProgramFlowMinute(string Time, double CumulativeNet, double Price);

/// <summary>The computed footprint signals for one stock (all deterministic).</summary>
/// <param name="WindowNet">Net program buying summed over the daily window (억원).</param>
/// <param name="TodayNet">The most recent day's net program buying (억원).</param>
/// <param name="BuyDays">Days in the window with net program buying.</param>
/// <param name="SellDays">Days in the window with net program selling.</param>
/// <param name="Streak">Consecutive same-direction days ending today; signed (+ buy / − sell).</param>
/// <param name="UpDayRatio">Fraction of window days that were net program buying.</param>
/// <param name="ChurnRatio">|window net| ÷ window gross — low = two-way churn, high = one-directional.</param>
/// <param name="Intensity">|today's net| ÷ the window's average |daily net|.</param>
/// <param name="PaceRegularity"><c>steady</c> / <c>bursty</c> / <c>n/a</c> — intraday execution regularity.</param>
/// <param name="PaceCv">Coefficient of variation of the per-minute net deltas.</param>
/// <param name="Loading"><c>front_loaded</c> / <c>back_loaded</c> / <c>even</c> / <c>n/a</c>.</param>
/// <param name="PriceCoupling">Pearson correlation of cumulative program net vs price, intraday.</param>
internal sealed record ProgramFlowSignals(
    double WindowNet,
    double TodayNet,
    int BuyDays,
    int SellDays,
    int Streak,
    double UpDayRatio,
    double ChurnRatio,
    double Intensity,
    string PaceRegularity,
    double PaceCv,
    string Loading,
    double PriceCoupling);

/// <summary>The institutional-footprint verdict for one stock.</summary>
/// <param name="Regime"><c>accumulation</c> / <c>distribution</c> / <c>churn</c> / <c>neutral</c>.</param>
/// <param name="DirectionConfidence">0–1 confidence in the regime call.</param>
/// <param name="Signals">The computed signals behind the verdict.</param>
/// <param name="Evidence">Plain-language findings for the Interpretation Layer to narrate.</param>
internal sealed record ProgramFootprintReport(
    string Regime,
    double DirectionConfidence,
    ProgramFlowSignals Signals,
    IReadOnlyList<string> Evidence);

/// <summary>
/// Analysis Layer — turns a stock's raw program-trading series (TR t1637) into a
/// deterministic institutional-footprint verdict. Pure functions, no I/O: the
/// caller fetches the data, this classifies it. The Interpretation Layer (the
/// LLM) narrates <see cref="ProgramFootprintReport.Evidence"/>.
/// </summary>
internal static class ProgramFootprintAnalyzer
{
    /// <summary>Below this |net|÷gross ratio the flow reads as two-way churn.</summary>
    const double ChurnThreshold = 0.15;

    /// <summary>Per-minute delta CV at or below this reads as steady (TWAP-like) execution.</summary>
    const double SteadyCvThreshold = 1.0;

    /// <summary>Share of the day's net flow in the open / close window that flags loading.</summary>
    const double LoadingFraction = 0.40;

    /// <summary>Upper cap for the pace CV so a near-zero mean stays JSON-finite.</summary>
    const double MaxPaceCv = 99.9;

    /// <summary>
    /// Classifies a stock's program-trading footprint from its daily history and
    /// today's intraday series.
    /// </summary>
    /// <param name="daily">Daily flow, chronological (oldest first). Must be non-empty.</param>
    /// <param name="intraday">Today's minute flow, chronological. May be empty (pre-open) —
    /// the intraday-derived signals then report <c>n/a</c>.</param>
    /// <returns>The footprint verdict.</returns>
    /// <exception cref="ArgumentException">The daily list is empty.</exception>
    public static ProgramFootprintReport Analyze(
        IReadOnlyList<ProgramFlowDay> daily,
        IReadOnlyList<ProgramFlowMinute> intraday)
    {
        ArgumentNullException.ThrowIfNull(daily);
        ArgumentNullException.ThrowIfNull(intraday);
        if (daily.Count == 0)
            throw new ArgumentException("daily must not be empty.", nameof(daily));

        double windowNet = daily.Sum(d => d.Net);
        double todayNet = daily[^1].Net;
        int buyDays = daily.Count(d => d.Net > 0);
        int sellDays = daily.Count(d => d.Net < 0);
        double upDayRatio = Math.Round((double)buyDays / daily.Count, 3);
        int streak = ComputeStreak(daily);

        double gross = daily.Sum(d => d.GrossBuy + d.GrossSell);
        double churnRatio = gross > 0 ? Math.Round(Math.Abs(windowNet) / gross, 3) : 0;

        double avgAbs = daily.Average(d => Math.Abs(d.Net));
        double intensity = avgAbs > 0 ? Math.Round(Math.Abs(todayNet) / avgAbs, 2) : 0;

        (string paceRegularity, double paceCv) = ComputePace(intraday);
        string loading = ComputeLoading(intraday);
        double priceCoupling = ComputeCoupling(intraday);

        var signals = new ProgramFlowSignals(
            WindowNet: Math.Round(windowNet, 1),
            TodayNet: Math.Round(todayNet, 1),
            BuyDays: buyDays,
            SellDays: sellDays,
            Streak: streak,
            UpDayRatio: upDayRatio,
            ChurnRatio: churnRatio,
            Intensity: intensity,
            PaceRegularity: paceRegularity,
            PaceCv: paceCv,
            Loading: loading,
            PriceCoupling: priceCoupling);

        string regime = ClassifyRegime(signals);
        double confidence = ComputeConfidence(signals, regime);
        IReadOnlyList<string> evidence = BuildEvidence(signals, regime, daily.Count, intraday.Count > 0);

        return new ProgramFootprintReport(regime, confidence, signals, evidence);
    }

    /// <summary>Consecutive same-direction days ending on the last day; signed (+ buy / − sell).</summary>
    static int ComputeStreak(IReadOnlyList<ProgramFlowDay> daily)
    {
        int lastSign = Math.Sign(daily[^1].Net);
        if (lastSign == 0) return 0;
        int n = 0;
        for (int i = daily.Count - 1; i >= 0; i--)
        {
            if (Math.Sign(daily[i].Net) != lastSign) break;
            n++;
        }
        return n * lastSign;
    }

    /// <summary>
    /// Per-minute net-delta regularity: a steady (TWAP-like) algo adds a roughly
    /// constant net each minute (low CV); a bursty program spikes (high CV).
    /// </summary>
    static (string Regularity, double Cv) ComputePace(IReadOnlyList<ProgramFlowMinute> intraday)
    {
        if (intraday.Count < 6) return ("n/a", 0);

        var deltas = new List<double>(intraday.Count - 1);
        for (int i = 1; i < intraday.Count; i++)
            deltas.Add(intraday[i].CumulativeNet - intraday[i - 1].CumulativeNet);

        double mean = deltas.Average();
        if (Math.Abs(mean) < 1e-9) return ("bursty", MaxPaceCv);

        double variance = deltas.Sum(d => (d - mean) * (d - mean)) / deltas.Count;
        double cv = Math.Min(Math.Sqrt(variance) / Math.Abs(mean), MaxPaceCv);
        cv = Math.Round(cv, 2);
        return (cv <= SteadyCvThreshold ? "steady" : "bursty", cv);
    }

    /// <summary>Where the session's net flow concentrated — open, close, or evenly.</summary>
    static string ComputeLoading(IReadOnlyList<ProgramFlowMinute> intraday)
    {
        if (intraday.Count < 10) return "n/a";

        double total = intraday[^1].CumulativeNet - intraday[0].CumulativeNet;
        if (Math.Abs(total) < 1e-9) return "even";

        int w = Math.Min(30, intraday.Count / 3);
        double first = intraday[w].CumulativeNet - intraday[0].CumulativeNet;
        double last = intraday[^1].CumulativeNet - intraday[^(w + 1)].CumulativeNet;

        if (first / total >= LoadingFraction) return "front_loaded";
        if (last / total >= LoadingFraction) return "back_loaded";
        return "even";
    }

    /// <summary>Pearson correlation of cumulative program net buying vs the price.</summary>
    static double ComputeCoupling(IReadOnlyList<ProgramFlowMinute> intraday)
    {
        if (intraday.Count < 6) return 0;

        double mx = intraday.Average(p => p.CumulativeNet);
        double my = intraday.Average(p => p.Price);
        double sxy = 0, sxx = 0, syy = 0;
        foreach (ProgramFlowMinute p in intraday)
        {
            double dx = p.CumulativeNet - mx, dy = p.Price - my;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }
        if (sxx <= 0 || syy <= 0) return 0;
        return Math.Round(sxy / Math.Sqrt(sxx * syy), 3);
    }

    /// <summary>
    /// Maps the signals onto a regime. Churn is checked first — when the net is
    /// tiny relative to the gross two-way flow, direction is not the story.
    /// </summary>
    static string ClassifyRegime(ProgramFlowSignals s)
    {
        if (s.ChurnRatio < ChurnThreshold && s.BuyDays >= 3 && s.SellDays >= 3)
            return "churn";
        if (s.WindowNet > 0 && (s.UpDayRatio >= 0.6 || s.Streak >= 3))
            return "accumulation";
        if (s.WindowNet < 0 && (s.UpDayRatio <= 0.4 || s.Streak <= -3))
            return "distribution";
        return "neutral";
    }

    /// <summary>Blends persistence, streak, intensity, and price coupling into a 0–1 score.</summary>
    static double ComputeConfidence(ProgramFlowSignals s, string regime)
    {
        if (regime == "neutral") return 0;
        if (regime == "churn")
            return Math.Round(Math.Clamp(1 - s.ChurnRatio / ChurnThreshold, 0, 1), 2);

        double persistence = Math.Abs(s.UpDayRatio - 0.5) * 2;
        double streakComp = Math.Min(Math.Abs(s.Streak) / 5.0, 1);
        double intensityComp = Math.Min(s.Intensity / 3.0, 1);
        double couplingComp = Math.Abs(s.PriceCoupling);
        double conf = persistence * 0.4 + streakComp * 0.3 + intensityComp * 0.15 + couplingComp * 0.15;
        return Math.Round(Math.Clamp(conf, 0, 1), 2);
    }

    /// <summary>Generates the plain-language evidence bullets for the Interpretation Layer.</summary>
    static List<string> BuildEvidence(
        ProgramFlowSignals s, string regime, int dayCount, bool hasIntraday)
    {
        string regimeKo = regime switch
        {
            "accumulation" => "매집(accumulation)",
            "distribution" => "분산(distribution)",
            "churn" => "양방향 churn",
            _ => "중립(neutral)",
        };

        var ev = new List<string>
        {
            $"최근 {dayCount}거래일 프로그램 순매수 합계 {s.WindowNet:N0}억 — {regimeKo} 국면",
            $"{dayCount}일 중 순매수 {s.BuyDays}일 / 순매도 {s.SellDays}일 (순매수 비율 {s.UpDayRatio:P0})",
        };

        if (Math.Abs(s.Streak) >= 2)
            ev.Add($"{Math.Abs(s.Streak)}일 연속 순{(s.Streak > 0 ? "매수" : "매도")}");
        if (s.Intensity >= 1.5)
            ev.Add($"오늘 순매수 강도 {s.Intensity:F1}배 (vs {dayCount}일 평균 일중 순매수)");
        ev.Add($"churn 비율 {s.ChurnRatio:P0} (낮을수록 매수·매도 양방향)");

        if (hasIntraday)
        {
            if (s.PaceRegularity == "steady")
                ev.Add($"장중 매매 페이스 균일 (분당 델타 CV {s.PaceCv:F2}) — TWAP형 알고 추정");
            else if (s.PaceRegularity == "bursty")
                ev.Add($"장중 매매 불규칙 (분당 델타 CV {s.PaceCv:F2}) — 버스트성 실행");

            if (s.Loading == "front_loaded")
                ev.Add("프로그램 순매수가 장 초반에 집중");
            else if (s.Loading == "back_loaded")
                ev.Add("프로그램 순매수가 장 마감에 집중");

            ev.Add($"누적 프로그램 순매수와 주가의 상관 {s.PriceCoupling:+0.00;-0.00;0.00}");
        }

        return ev;
    }
}
