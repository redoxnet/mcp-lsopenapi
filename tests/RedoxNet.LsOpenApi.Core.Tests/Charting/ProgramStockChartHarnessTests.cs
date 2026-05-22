using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Charting;
using Xunit;
using Xunit.Abstractions;

namespace RedoxNet.LsOpenApi.Core.Tests.Charting;

/// <summary>
/// Renders <see cref="ProgramStockChartBuilder"/> against real t1637
/// (종목별 프로그램매매 추이) captures into standalone, browser-viewable HTML files.
/// Visual dev aid — open the <c>chart-output/*.html</c> path each test logs; the
/// assertion only sanity-checks the expected trace type.
/// </summary>
public class ProgramStockChartHarnessTests
{
    readonly ITestOutputHelper _output;

    /// <summary>Captures the xUnit output sink so each test can log its HTML path.</summary>
    public ProgramStockChartHarnessTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// IntradayFlow from the real 2026-05-22 삼성전자 capture: the stock price
    /// against cumulative program net buying — a heavy program sell session.
    /// t1637 intraday only ever returns the current session, so the capture
    /// runs to the moment it was taken (10:35), not the close.
    /// </summary>
    [Fact]
    public void IntradayFlow_RealCapture_WritesViewableHtml()
    {
        IReadOnlyList<ProgramStockPoint> points = ParseIntraday();
        var meta = new ProgramStockChartMeta("005930", "삼성전자", "2026-05-22");

        JsonObject envelope = ProgramStockChartBuilder.Build(
            ProgramStockChartView.IntradayFlow, meta, points);
        string path = ChartHtmlHarness.Write("program-trading-stock-intraday", envelope["spec"]!);

        _output.WriteLine("chart written — open in a browser:\n  " + path);
        File.ReadAllText(path).Should().Contain("\"scatter\"");
    }

    /// <summary>
    /// DailyBars from the real capture: per-day program net buying (red / blue)
    /// against the closing price over the most recent ~45 sessions.
    /// </summary>
    [Fact]
    public void DailyBars_RealCapture_WritesViewableHtml()
    {
        IReadOnlyList<ProgramStockPoint> points = ParseDaily();
        var meta = new ProgramStockChartMeta("005930", "삼성전자", "최근 45일");

        JsonObject envelope = ProgramStockChartBuilder.Build(
            ProgramStockChartView.DailyBars, meta, points);
        string path = ChartHtmlHarness.Write("program-trading-stock-daily", envelope["spec"]!);

        _output.WriteLine("chart written — open in a browser:\n  " + path);
        File.ReadAllText(path).Should().Contain("\"bar\"");
    }

    /// <summary>Parses the intraday capture (newest-first) into chronological points.</summary>
    static IReadOnlyList<ProgramStockPoint> ParseIntraday()
    {
        using JsonDocument doc = JsonDocument.Parse(IntradayCapture);
        var list = new List<ProgramStockPoint>();
        foreach (JsonElement r in doc.RootElement.GetProperty("t1637OutBlock1").EnumerateArray())
        {
            string t = r.GetProperty("time").GetString()!;
            list.Add(new ProgramStockPoint(
                Label: $"{t[..2]}:{t.Substring(2, 2)}",
                Price: r.GetProperty("price").GetInt64(),
                Net: r.GetProperty("svalue").GetInt64() / 100_000.0));
        }
        list.Reverse();   // LS ships newest-first
        return list;
    }

    /// <summary>Parses the daily capture (newest-first) into chronological points.</summary>
    static IReadOnlyList<ProgramStockPoint> ParseDaily()
    {
        using JsonDocument doc = JsonDocument.Parse(DailyCapture);
        var list = new List<ProgramStockPoint>();
        foreach (JsonElement r in doc.RootElement.GetProperty("t1637OutBlock1").EnumerateArray())
        {
            string d = r.GetProperty("date").GetString()!;
            list.Add(new ProgramStockPoint(
                Label: $"{d[..4]}-{d.Substring(4, 2)}-{d.Substring(6, 2)}",
                Price: r.GetProperty("price").GetInt64(),
                Net: r.GetProperty("svalue").GetInt64() / 100_000.0));
        }
        list.Reverse();
        return list;
    }

    /// <summary>
    /// Real 삼성전자 t1637 intraday capture (gubun2=시간), 2026-05-22 09:01–10:35.
    /// t1637 intraday is current-session-only, so the series ends at the capture
    /// time. <c>svalue</c> is cumulative program net buying, LS amount-basis 천원.
    /// </summary>
    const string IntradayCapture = """
        {"t1637OutBlock":{"cts_idx":0},"rsp_cd":"00000","t1637OutBlock1":[
        {"time":"103500","price":294000,"svalue":-393180046},{"time":"103400","price":294250,"svalue":-388935334},{"time":"103300","price":294250,"svalue":-387498096},{"time":"103200","price":294000,"svalue":-384835308},{"time":"103100","price":294000,"svalue":-380327232},
        {"time":"103000","price":293500,"svalue":-376322498},{"time":"102900","price":294000,"svalue":-374420918},{"time":"102800","price":294000,"svalue":-376869650},{"time":"102700","price":294500,"svalue":-376861232},{"time":"102600","price":294250,"svalue":-376076334},
        {"time":"102500","price":294250,"svalue":-375717010},{"time":"102400","price":294250,"svalue":-371919618},{"time":"102300","price":294250,"svalue":-371135205},{"time":"102200","price":294750,"svalue":-365100096},{"time":"102100","price":295000,"svalue":-365789828},
        {"time":"102000","price":295000,"svalue":-366006705},{"time":"101900","price":294750,"svalue":-365239833},{"time":"101800","price":294500,"svalue":-362706345},{"time":"101700","price":294500,"svalue":-362771489},{"time":"101600","price":294250,"svalue":-362103792},
        {"time":"101500","price":294500,"svalue":-360928099},{"time":"101400","price":294500,"svalue":-357983590},{"time":"101300","price":293750,"svalue":-356109014},{"time":"101200","price":293750,"svalue":-354883005},{"time":"101100","price":293500,"svalue":-353116573},
        {"time":"101000","price":293250,"svalue":-352554619},{"time":"100900","price":293000,"svalue":-349840120},{"time":"100800","price":292500,"svalue":-348831062},{"time":"100700","price":292500,"svalue":-351251766},{"time":"100600","price":292500,"svalue":-349335778},
        {"time":"100500","price":293500,"svalue":-348739367},{"time":"100400","price":293250,"svalue":-345444788},{"time":"100300","price":292250,"svalue":-342388464},{"time":"100200","price":292000,"svalue":-337264751},{"time":"100100","price":292750,"svalue":-333331062},
        {"time":"100000","price":293500,"svalue":-333793839},{"time":"095900","price":292500,"svalue":-330495736},{"time":"095800","price":292750,"svalue":-334914450},{"time":"095700","price":293250,"svalue":-331409687},{"time":"095600","price":293500,"svalue":-328115420},
        {"time":"095500","price":294000,"svalue":-326406571},{"time":"095400","price":294000,"svalue":-323233606},{"time":"095300","price":294250,"svalue":-297894037},{"time":"095200","price":295250,"svalue":-277354444},{"time":"095100","price":295500,"svalue":-275422022},
        {"time":"095000","price":295000,"svalue":-274668874},{"time":"094900","price":295000,"svalue":-252544388},{"time":"094800","price":295500,"svalue":-241943419},{"time":"094700","price":296250,"svalue":-233701006},{"time":"094600","price":296000,"svalue":-229223301},
        {"time":"094500","price":295500,"svalue":-221150078},{"time":"094400","price":296000,"svalue":-218916097},{"time":"094300","price":296000,"svalue":-217849069},{"time":"094200","price":295750,"svalue":-216049578},{"time":"094100","price":296250,"svalue":-215956929},
        {"time":"094000","price":296250,"svalue":-210881270},{"time":"093900","price":296000,"svalue":-210201507},{"time":"093800","price":295500,"svalue":-209399015},{"time":"093700","price":295500,"svalue":-206811315},{"time":"093600","price":295000,"svalue":-213804838},
        {"time":"093500","price":295000,"svalue":-202145487},{"time":"093400","price":295000,"svalue":-203777790},{"time":"093300","price":295500,"svalue":-201855759},{"time":"093200","price":295500,"svalue":-202545658},{"time":"093100","price":295750,"svalue":-200672378},
        {"time":"093000","price":295500,"svalue":-199672780},{"time":"092900","price":295500,"svalue":-199529952},{"time":"092800","price":295500,"svalue":-190053543},{"time":"092700","price":295500,"svalue":-189995601},{"time":"092600","price":295750,"svalue":-189061483},
        {"time":"092500","price":296750,"svalue":-182228398},{"time":"092400","price":297000,"svalue":-178591564},{"time":"092300","price":296500,"svalue":-172764681},{"time":"092200","price":297000,"svalue":-166261798},{"time":"092100","price":296000,"svalue":-158776462},
        {"time":"092000","price":295500,"svalue":-157020663},{"time":"091900","price":294750,"svalue":-153078797},{"time":"091800","price":295000,"svalue":-152327012},{"time":"091700","price":294750,"svalue":-147282677},{"time":"091600","price":295000,"svalue":-139041416},
        {"time":"091500","price":295500,"svalue":-134129996},{"time":"091400","price":296000,"svalue":-133582341},{"time":"091300","price":296250,"svalue":-133413360},{"time":"091200","price":296500,"svalue":-135019130},{"time":"091100","price":296000,"svalue":-129569268},
        {"time":"091000","price":296000,"svalue":-135447110},{"time":"090900","price":296500,"svalue":-130987278},{"time":"090800","price":296750,"svalue":-120270978},{"time":"090700","price":297500,"svalue":-102284829},{"time":"090600","price":298000,"svalue":-96402683},
        {"time":"090500","price":296750,"svalue":-64962982},{"time":"090400","price":298000,"svalue":-35009458},{"time":"090300","price":298750,"svalue":-28144052},{"time":"090200","price":300000,"svalue":-16937540},{"time":"090100","price":298500,"svalue":-6272685}
        ]}
        """;

    /// <summary>
    /// Real 삼성전자 t1637 daily capture (gubun2=일자), the most recent 45 sessions.
    /// <c>svalue</c> is that day's program net buying, LS amount-basis 천원.
    /// </summary>
    const string DailyCapture = """
        {"t1637OutBlock":{"cts_idx":0},"rsp_cd":"00000","t1637OutBlock1":[
        {"date":"20260522","price":296000,"svalue":-210139889},{"date":"20260521","price":299500,"svalue":1706786691},
        {"date":"20260520","price":276000,"svalue":-760311247},{"date":"20260519","price":275500,"svalue":-2134261970},
        {"date":"20260518","price":281000,"svalue":-659755415},{"date":"20260515","price":270500,"svalue":-2189719213},
        {"date":"20260514","price":296000,"svalue":1751394686},{"date":"20260513","price":284000,"svalue":-1124165175},
        {"date":"20260512","price":279000,"svalue":-1066950486},{"date":"20260511","price":285500,"svalue":-1975120018},
        {"date":"20260508","price":268500,"svalue":-1659977894},{"date":"20260507","price":271500,"svalue":-1737029628},
        {"date":"20260506","price":266000,"svalue":1552256161},{"date":"20260504","price":232500,"svalue":1373185053},
        {"date":"20260430","price":220500,"svalue":-123993721},{"date":"20260429","price":226000,"svalue":695712282},
        {"date":"20260428","price":222000,"svalue":-283087645},{"date":"20260427","price":224500,"svalue":832882100},
        {"date":"20260424","price":219500,"svalue":-808989881},{"date":"20260423","price":224500,"svalue":1145145065},
        {"date":"20260422","price":217500,"svalue":-105269362},{"date":"20260421","price":219000,"svalue":115271030},
        {"date":"20260420","price":214500,"svalue":-490956767},{"date":"20260417","price":216000,"svalue":-411604040},
        {"date":"20260416","price":217500,"svalue":549338993},{"date":"20260415","price":211000,"svalue":45453877},
        {"date":"20260414","price":206500,"svalue":-15878918},{"date":"20260413","price":201000,"svalue":-168486422},
        {"date":"20260410","price":206000,"svalue":-21859728},{"date":"20260409","price":204000,"svalue":-987320038},
        {"date":"20260408","price":210500,"svalue":321205321},{"date":"20260407","price":196500,"svalue":-444795552},
        {"date":"20260406","price":193100,"svalue":218946040},{"date":"20260403","price":186200,"svalue":213527843},
        {"date":"20260402","price":178400,"svalue":-352171986},{"date":"20260401","price":189600,"svalue":424822242},
        {"date":"20260331","price":167200,"svalue":-1121726368},{"date":"20260330","price":176300,"svalue":-182058479},
        {"date":"20260327","price":179700,"svalue":-873712128},{"date":"20260326","price":180100,"svalue":-1157299524},
        {"date":"20260325","price":189000,"svalue":-870923033},{"date":"20260324","price":189700,"svalue":-932560305},
        {"date":"20260323","price":186300,"svalue":-1100792342},{"date":"20260320","price":199400,"svalue":-1249868544},
        {"date":"20260319","price":200500,"svalue":-676519292}
        ]}
        """;
}
