using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Charting;
using Xunit;
using Xunit.Abstractions;

namespace RedoxNet.LsOpenApi.Core.Tests.Charting;

/// <summary>
/// Renders <see cref="ProgramTradeChartBuilder"/> against a real (downsampled)
/// t1662 capture into a standalone, browser-viewable HTML file. Visual dev aid —
/// open the <c>chart-output/*.html</c> path the test logs to eyeball the chart;
/// the assertion only sanity-checks that a chart of the expected trace type came
/// out. Using a real capture (not synthetic data) keeps the numbers, the
/// irregular minute spacing, and the 15:30 closing-auction record faithful.
/// </summary>
public class ProgramTradeChartHarnessTests
{
    readonly ITestOutputHelper _output;

    /// <summary>Captures the xUnit output sink so the test can log its HTML path.</summary>
    public ProgramTradeChartHarnessTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// FlowOverview from the real 2026-05-21 KOSPI t1662 capture: K200 on the
    /// left axis, cumulative 전체 / 비차익 / 차익 net buying (억원) on the right,
    /// the 15:20–15:30 closing auction shaded, and trough / 15:30 / EOD annotated.
    /// </summary>
    [Fact]
    public void FlowOverview_RealCapture_WritesViewableHtml()
    {
        IReadOnlyList<ProgramTradeChartBuilder.ProgramFlowPoint> points = ParseCapture();
        var meta = new ProgramChartMeta("kospi", "today", "2026-05-21", "금액 (억원)");

        JsonObject envelope = ProgramTradeChartBuilder.Build(
            ProgramTradeChartView.FlowOverview, meta, points);
        string path = ChartHtmlHarness.Write("program-trading-flow-overview", envelope["spec"]!);

        _output.WriteLine("chart written — open in a browser:\n  " + path);
        File.ReadAllText(path).Should().Contain("\"scatter\"");
    }

    /// <summary>
    /// BasisArbitrage from the real capture: KOSPI200 futures basis on the left
    /// axis vs cumulative arbitrage net buying (filled) on the right.
    /// </summary>
    [Fact]
    public void BasisArbitrage_RealCapture_WritesViewableHtml()
    {
        IReadOnlyList<ProgramTradeChartBuilder.ProgramFlowPoint> points = ParseCapture();
        var meta = new ProgramChartMeta("kospi", "today", "2026-05-21", "금액 (억원)");

        JsonObject envelope = ProgramTradeChartBuilder.Build(
            ProgramTradeChartView.BasisArbitrage, meta, points);
        string path = ChartHtmlHarness.Write("program-trading-basis-arbitrage", envelope["spec"]!);

        _output.WriteLine("chart written — open in a browser:\n  " + path);
        File.ReadAllText(path).Should().Contain("\"scatter\"");
    }

    /// <summary>
    /// IntensityBars from the real capture: per-minute net buying as stacked
    /// bars (비차익 + 차익), highlighting open / closing-auction spikes.
    /// </summary>
    [Fact]
    public void IntensityBars_RealCapture_WritesViewableHtml()
    {
        IReadOnlyList<ProgramTradeChartBuilder.ProgramFlowPoint> points = ParseCapture();
        var meta = new ProgramChartMeta("kospi", "today", "2026-05-21", "금액 (억원)");

        JsonObject envelope = ProgramTradeChartBuilder.Build(
            ProgramTradeChartView.IntensityBars, meta, points);
        string path = ChartHtmlHarness.Write("program-trading-intensity-bars", envelope["spec"]!);

        _output.WriteLine("chart written — open in a browser:\n  " + path);
        File.ReadAllText(path).Should().Contain("\"bar\"");
    }

    /// <summary>
    /// GrossFlow from the real capture: per-5-minute gross 매수 / 매도 in two
    /// stacked panels — 비차익 (top) and 차익 (bottom) — separating
    /// one-directional accumulation from two-way churn.
    /// </summary>
    [Fact]
    public void GrossFlow_RealCapture_WritesViewableHtml()
    {
        IReadOnlyList<ProgramTradeChartBuilder.ProgramFlowPoint> points = ParseCapture();
        var meta = new ProgramChartMeta("kospi", "today", "2026-05-21", "금액 (억원)");

        JsonObject envelope = ProgramTradeChartBuilder.Build(
            ProgramTradeChartView.GrossFlow, meta, points);
        string path = ChartHtmlHarness.Write("program-trading-gross-flow", envelope["spec"]!);

        _output.WriteLine("chart written — open in a browser:\n  " + path);
        File.ReadAllText(path).Should().Contain("\"bar\"");
    }

    /// <summary>
    /// MarketDaily from the real KOSPI t1633 capture: per-day 비차익 / 차익 net
    /// buying as stacked bars against the KOSPI200 index over ~40 sessions.
    /// </summary>
    [Fact]
    public void MarketDaily_RealCapture_WritesViewableHtml()
    {
        IReadOnlyList<ProgramTradeChartBuilder.ProgramFlowPoint> points = ParseT1633Capture();
        var meta = new ProgramChartMeta("kospi", "today", "2026-03-26 ~ 2026-05-22", "금액 (억원)");

        JsonObject envelope = ProgramTradeChartBuilder.Build(
            ProgramTradeChartView.MarketDaily, meta, points);
        string path = ChartHtmlHarness.Write("program-trading-market-daily", envelope["spec"]!);

        _output.WriteLine("chart written — open in a browser:\n  " + path);
        File.ReadAllText(path).Should().Contain("\"bar\"");
    }

    /// <summary>
    /// Parses the embedded t1662 capture into chart points. t1662 ships amounts
    /// in 백만원; the chart shows 억원 (= 백만원 ÷ 100, the Naver scale). The
    /// fields are cumulative from the session open, so minute_net is the diff of
    /// consecutive rows.
    /// </summary>
    static IReadOnlyList<ProgramTradeChartBuilder.ProgramFlowPoint> ParseCapture()
    {
        using JsonDocument doc = JsonDocument.Parse(RealT1662Capture);
        JsonElement rows = doc.RootElement.GetProperty("t1662OutBlock");

        var points = new List<ProgramTradeChartBuilder.ProgramFlowPoint>(rows.GetArrayLength());
        double prevNet = 0;
        bool first = true;
        foreach (JsonElement r in rows.EnumerateArray())
        {
            string raw = r.GetProperty("time").GetString()!;
            string time = $"{raw[..2]}:{raw.Substring(2, 2)}";

            double net = r.GetProperty("tot3").GetInt64() / 100.0;
            double minuteNet = first ? net : net - prevNet;
            first = false;
            prevNet = net;

            points.Add(new ProgramTradeChartBuilder.ProgramFlowPoint(
                Time: time,
                Kospi200: ParseDouble(r, "k200jisu"),
                Basis: ParseDouble(r, "k200basis"),
                Net: net,
                Arbitrage: r.GetProperty("cha3").GetInt64() / 100.0,
                NonArbitrage: r.GetProperty("bcha3").GetInt64() / 100.0,
                MinuteNet: minuteNet,
                ArbitrageBuy: r.GetProperty("cha1").GetInt64() / 100.0,
                ArbitrageSell: r.GetProperty("cha2").GetInt64() / 100.0,
                NonArbitrageBuy: r.GetProperty("bcha1").GetInt64() / 100.0,
                NonArbitrageSell: r.GetProperty("bcha2").GetInt64() / 100.0));
        }
        return points;
    }

    static double ParseDouble(JsonElement row, string field) =>
        double.Parse(row.GetProperty(field).GetString()!, CultureInfo.InvariantCulture);

    /// <summary>
    /// A real KOSPI t1662 (시간대별 프로그램매매 추이) response from 2026-05-21,
    /// downsampled to ~5-minute spacing with the 15:18–15:32 closing-auction
    /// window kept at 1-minute resolution. Carries the cumulative net fields
    /// (tot3 / cha3 / bcha3) plus the gross buy / sell legs (cha1·cha2 for
    /// 차익, bcha1·bcha2 for 비차익) that GrossFlow decomposes; amounts are LS
    /// amount-basis (gubun1=0, 백만원).
    /// </summary>
    const string RealT1662Capture = """
        {"rsp_cd":"00000","rsp_msg":"OK","t1662OutBlock":[{"time":"090100","k200jisu":"1175.45","k200basis":"0.45","tot3":-276123,"cha1":11602,"cha2":6265,"cha3":5338,"bcha1":508305,"bcha2":789765,"bcha3":-281460},{"time":"090900","k200jisu":"1177.98","k200basis":"0.42","tot3":-383394,"cha1":29473,"cha2":22755,"cha3":6717,"bcha1":1257973,"bcha2":1648084,"bcha3":-390111},{"time":"091700","k200jisu":"1180.39","k200basis":"1.16","tot3":-351190,"cha1":58956,"cha2":22756,"cha3":36199,"bcha1":1712384,"bcha2":2099773,"bcha3":-387390},{"time":"092400","k200jisu":"1181.62","k200basis":"0.88","tot3":-294282,"cha1":58956,"cha2":34581,"cha3":24375,"bcha1":2073088,"bcha2":2391746,"bcha3":-318657},{"time":"092900","k200jisu":"1183.28","k200basis":"2.17","tot3":-434703,"cha1":58956,"cha2":34582,"cha3":24374,"bcha1":2089228,"bcha2":2548304,"bcha3":-459076},{"time":"093600","k200jisu":"1189.67","k200basis":"1.08","tot3":-193835,"cha1":58956,"cha2":52900,"cha3":6056,"bcha1":2621755,"bcha2":2821646,"bcha3":-199891},{"time":"094300","k200jisu":"1192.04","k200basis":"0.21","tot3":-127191,"cha1":70867,"cha2":64844,"cha3":6023,"bcha1":2911785,"bcha2":3045000,"bcha3":-133214},{"time":"095000","k200jisu":"1194.37","k200basis":"0.63","tot3":-41348,"cha1":76821,"cha2":66265,"cha3":10556,"bcha1":3162821,"bcha2":3214725,"bcha3":-51904},{"time":"095600","k200jisu":"1195.92","k200basis":"1.23","tot3":48963,"cha1":82799,"cha2":66706,"cha3":16093,"bcha1":3416633,"bcha2":3383763,"bcha3":32870},{"time":"100200","k200jisu":"1195.98","k200basis":"1.42","tot3":104002,"cha1":94764,"cha2":66733,"cha3":28032,"bcha1":3657654,"bcha2":3581683,"bcha3":75970},{"time":"100800","k200jisu":"1197.27","k200basis":"1.03","tot3":173371,"cha1":100761,"cha2":72697,"cha3":28064,"bcha1":3872423,"bcha2":3727116,"bcha3":145307},{"time":"101400","k200jisu":"1198.49","k200basis":"1.21","tot3":222370,"cha1":100763,"cha2":73196,"cha3":27567,"bcha1":4021858,"bcha2":3827054,"bcha3":194803},{"time":"102000","k200jisu":"1200.99","k200basis":"1.26","tot3":286184,"cha1":124393,"cha2":73196,"cha3":51197,"bcha1":4179730,"bcha2":3944743,"bcha3":234987},{"time":"102600","k200jisu":"1203.25","k200basis":"0.75","tot3":382194,"cha1":130555,"cha2":85161,"cha3":45394,"bcha1":4399185,"bcha2":4062385,"bcha3":336800},{"time":"103200","k200jisu":"1202.98","k200basis":"1.47","tot3":469360,"cha1":148684,"cha2":85231,"cha3":63453,"bcha1":4599964,"bcha2":4194056,"bcha3":405907},{"time":"103800","k200jisu":"1205.15","k200basis":"2.20","tot3":577055,"cha1":148905,"cha2":85732,"cha3":63173,"bcha1":4822982,"bcha2":4309100,"bcha3":513882},{"time":"104400","k200jisu":"1204.07","k200basis":"1.73","tot3":644435,"cha1":190121,"cha2":85732,"cha3":104389,"bcha1":5084612,"bcha2":4544567,"bcha3":540045},{"time":"105000","k200jisu":"1202.18","k200basis":"1.37","tot3":525054,"cha1":208980,"cha2":91704,"cha3":117276,"bcha1":5228520,"bcha2":4820742,"bcha3":407778},{"time":"105600","k200jisu":"1197.90","k200basis":"2.15","tot3":434968,"cha1":214985,"cha2":103639,"cha3":111346,"bcha1":5356674,"bcha2":5033052,"bcha3":323622},{"time":"110200","k200jisu":"1202.51","k200basis":"1.79","tot3":428143,"cha1":233090,"cha2":103749,"cha3":129341,"bcha1":5525438,"bcha2":5226636,"bcha3":298803},{"time":"110700","k200jisu":"1203.09","k200basis":"1.61","tot3":468292,"cha1":233306,"cha2":103759,"cha3":129548,"bcha1":5669832,"bcha2":5331088,"bcha3":338744},{"time":"111300","k200jisu":"1202.59","k200basis":"1.86","tot3":458294,"cha1":239208,"cha2":121568,"cha3":117640,"bcha1":5803626,"bcha2":5462973,"bcha3":340653},{"time":"111800","k200jisu":"1204.59","k200basis":"2.11","tot3":493530,"cha1":244999,"cha2":121828,"cha3":123171,"bcha1":5929053,"bcha2":5558695,"bcha3":370359},{"time":"112400","k200jisu":"1205.99","k200basis":"2.21","tot3":536143,"cha1":245425,"cha2":133683,"cha3":111742,"bcha1":6076521,"bcha2":5652120,"bcha3":424401},{"time":"112900","k200jisu":"1209.05","k200basis":"1.75","tot3":529551,"cha1":245425,"cha2":157882,"cha3":87543,"bcha1":6242274,"bcha2":5800267,"bcha3":442008},{"time":"113500","k200jisu":"1211.32","k200basis":"1.63","tot3":557934,"cha1":251053,"cha2":158140,"cha3":92913,"bcha1":6409818,"bcha2":5944797,"bcha3":465020},{"time":"114000","k200jisu":"1212.78","k200basis":"1.12","tot3":608105,"cha1":251442,"cha2":163899,"cha3":87543,"bcha1":6554066,"bcha2":6033503,"bcha3":520562},{"time":"114500","k200jisu":"1210.24","k200basis":"2.16","tot3":607965,"cha1":257291,"cha2":170321,"cha3":86969,"bcha1":6679534,"bcha2":6158538,"bcha3":520996},{"time":"115000","k200jisu":"1210.24","k200basis":"2.21","tot3":602450,"cha1":263590,"cha2":170346,"cha3":93244,"bcha1":6742204,"bcha2":6232998,"bcha3":509206},{"time":"115500","k200jisu":"1211.28","k200basis":"2.27","tot3":629475,"cha1":272858,"cha2":170350,"cha3":102507,"bcha1":6846587,"bcha2":6319620,"bcha3":526967},{"time":"120100","k200jisu":"1211.01","k200basis":"2.84","tot3":644355,"cha1":305142,"cha2":170353,"cha3":134789,"bcha1":6951476,"bcha2":6441911,"bcha3":509566},{"time":"120600","k200jisu":"1210.38","k200basis":"2.62","tot3":642585,"cha1":309564,"cha2":170353,"cha3":139212,"bcha1":7028853,"bcha2":6525479,"bcha3":503374},{"time":"121100","k200jisu":"1213.84","k200basis":"2.06","tot3":649141,"cha1":322311,"cha2":170353,"cha3":151958,"bcha1":7119370,"bcha2":6622188,"bcha3":497182},{"time":"121600","k200jisu":"1212.47","k200basis":"2.63","tot3":649925,"cha1":324193,"cha2":170353,"cha3":153840,"bcha1":7195134,"bcha2":6699049,"bcha3":496085},{"time":"122100","k200jisu":"1213.57","k200basis":"2.88","tot3":684381,"cha1":342027,"cha2":176267,"cha3":165759,"bcha1":7287732,"bcha2":6769111,"bcha3":518621},{"time":"122600","k200jisu":"1214.10","k200basis":"2.80","tot3":718950,"cha1":348531,"cha2":179998,"cha3":168533,"bcha1":7397852,"bcha2":6847434,"bcha3":550418},{"time":"123100","k200jisu":"1216.05","k200basis":"3.35","tot3":733271,"cha1":360499,"cha2":182480,"cha3":178018,"bcha1":7469394,"bcha2":6914141,"bcha3":555253},{"time":"123600","k200jisu":"1216.28","k200basis":"2.52","tot3":755991,"cha1":360817,"cha2":182509,"cha3":178308,"bcha1":7550655,"bcha2":6972971,"bcha3":577684},{"time":"124200","k200jisu":"1216.42","k200basis":"2.23","tot3":806543,"cha1":360836,"cha2":182513,"cha3":178323,"bcha1":7688299,"bcha2":7060079,"bcha3":628220},{"time":"124700","k200jisu":"1217.36","k200basis":"2.94","tot3":829357,"cha1":360836,"cha2":182513,"cha3":178323,"bcha1":7796642,"bcha2":7145608,"bcha3":651034},{"time":"125200","k200jisu":"1216.20","k200basis":"2.60","tot3":839542,"cha1":374364,"cha2":182513,"cha3":191851,"bcha1":7916007,"bcha2":7268315,"bcha3":647692},{"time":"125700","k200jisu":"1215.76","k200basis":"3.24","tot3":860475,"cha1":375986,"cha2":188420,"cha3":187566,"bcha1":8014879,"bcha2":7341970,"bcha3":672909},{"time":"130200","k200jisu":"1217.58","k200basis":"2.57","tot3":884713,"cha1":382656,"cha2":188583,"cha3":194073,"bcha1":8102607,"bcha2":7411967,"bcha3":690640},{"time":"130800","k200jisu":"1218.18","k200basis":"2.62","tot3":924295,"cha1":392004,"cha2":193764,"cha3":198241,"bcha1":8216989,"bcha2":7490935,"bcha3":726054},{"time":"131300","k200jisu":"1217.49","k200basis":"3.21","tot3":952590,"cha1":397376,"cha2":210559,"cha3":186817,"bcha1":8338222,"bcha2":7572450,"bcha3":765772},{"time":"131800","k200jisu":"1216.32","k200basis":"3.28","tot3":992332,"cha1":408524,"cha2":230108,"cha3":178416,"bcha1":8469185,"bcha2":7655269,"bcha3":813916},{"time":"132400","k200jisu":"1218.17","k200basis":"2.93","tot3":1021324,"cha1":423849,"cha2":243288,"cha3":180560,"bcha1":8579707,"bcha2":7738943,"bcha3":840764},{"time":"132900","k200jisu":"1217.78","k200basis":"2.97","tot3":1040153,"cha1":436848,"cha2":243494,"cha3":193354,"bcha1":8670553,"bcha2":7823753,"bcha3":846800},{"time":"133400","k200jisu":"1216.06","k200basis":"2.94","tot3":1010529,"cha1":449168,"cha2":255471,"cha3":193697,"bcha1":8767538,"bcha2":7950706,"bcha3":816832},{"time":"134000","k200jisu":"1217.89","k200basis":"3.31","tot3":1030298,"cha1":453290,"cha2":266033,"cha3":187257,"bcha1":8894463,"bcha2":8051422,"bcha3":843041},{"time":"134600","k200jisu":"1221.01","k200basis":"2.54","tot3":1082401,"cha1":462261,"cha2":274271,"cha3":187990,"bcha1":9037716,"bcha2":8143305,"bcha3":894411},{"time":"135100","k200jisu":"1220.48","k200basis":"1.92","tot3":1121277,"cha1":464464,"cha2":286871,"cha3":177593,"bcha1":9194490,"bcha2":8250806,"bcha3":943684},{"time":"135700","k200jisu":"1220.18","k200basis":"2.62","tot3":1169049,"cha1":468463,"cha2":334870,"cha3":133593,"bcha1":9384672,"bcha2":8349216,"bcha3":1035455},{"time":"140300","k200jisu":"1221.12","k200basis":"1.93","tot3":1231299,"cha1":468476,"cha2":338752,"cha3":129724,"bcha1":9558884,"bcha2":8457309,"bcha3":1101576},{"time":"140900","k200jisu":"1220.31","k200basis":"2.84","tot3":1201884,"cha1":468477,"cha2":384263,"cha3":84214,"bcha1":9728685,"bcha2":8611014,"bcha3":1117670},{"time":"141500","k200jisu":"1220.73","k200basis":"2.27","tot3":1221863,"cha1":468477,"cha2":414520,"cha3":53956,"bcha1":9907094,"bcha2":8739188,"bcha3":1167907},{"time":"142100","k200jisu":"1222.37","k200basis":"2.48","tot3":1260511,"cha1":468477,"cha2":421860,"cha3":46617,"bcha1":10078175,"bcha2":8864282,"bcha3":1213894},{"time":"142700","k200jisu":"1220.02","k200basis":"2.43","tot3":1285481,"cha1":468477,"cha2":436600,"cha3":31876,"bcha1":10273033,"bcha2":9019428,"bcha3":1253605},{"time":"143300","k200jisu":"1222.54","k200basis":"1.71","tot3":1312687,"cha1":468477,"cha2":440459,"cha3":28017,"bcha1":10459299,"bcha2":9174630,"bcha3":1284669},{"time":"143900","k200jisu":"1222.32","k200basis":"2.03","tot3":1368546,"cha1":468477,"cha2":444972,"cha3":23504,"bcha1":10657412,"bcha2":9312369,"bcha3":1345042},{"time":"144600","k200jisu":"1222.70","k200basis":"1.35","tot3":1435317,"cha1":474256,"cha2":445311,"cha3":28946,"bcha1":10857651,"bcha2":9451280,"bcha3":1406371},{"time":"145200","k200jisu":"1222.53","k200basis":"1.87","tot3":1516360,"cha1":478308,"cha2":453663,"cha3":24645,"bcha1":11136887,"bcha2":9645172,"bcha3":1491715},{"time":"150000","k200jisu":"1223.89","k200basis":"1.36","tot3":1726774,"cha1":492821,"cha2":465108,"cha3":27713,"bcha1":11543936,"bcha2":9844876,"bcha3":1699061},{"time":"150800","k200jisu":"1225.01","k200basis":"2.04","tot3":1863817,"cha1":493914,"cha2":466872,"cha3":27042,"bcha1":11918610,"bcha2":10081835,"bcha3":1836775},{"time":"151700","k200jisu":"1222.55","k200basis":"0.90","tot3":2041974,"cha1":499137,"cha2":481391,"cha3":17746,"bcha1":12460781,"bcha2":10436553,"bcha3":2024228},{"time":"151800","k200jisu":"1223.33","k200basis":"1.57","tot3":2052864,"cha1":499137,"cha2":481420,"cha3":17717,"bcha1":12543831,"bcha2":10508684,"bcha3":2035147},{"time":"152000","k200jisu":"1223.86","k200basis":"-0.36","tot3":2063002,"cha1":499137,"cha2":481444,"cha3":17692,"bcha1":12612199,"bcha2":10566889,"bcha3":2045309},{"time":"152100","k200jisu":"1223.86","k200basis":"-0.06","tot3":2063002,"cha1":499137,"cha2":481444,"cha3":17692,"bcha1":12612199,"bcha2":10566889,"bcha3":2045309},{"time":"152200","k200jisu":"1223.86","k200basis":"0.79","tot3":2063002,"cha1":499137,"cha2":481444,"cha3":17692,"bcha1":12612199,"bcha2":10566889,"bcha3":2045309},{"time":"152300","k200jisu":"1223.86","k200basis":"0.79","tot3":2063002,"cha1":499137,"cha2":481444,"cha3":17692,"bcha1":12612199,"bcha2":10566889,"bcha3":2045309},{"time":"152400","k200jisu":"1223.86","k200basis":"0.04","tot3":2063002,"cha1":499137,"cha2":481444,"cha3":17692,"bcha1":12612199,"bcha2":10566889,"bcha3":2045309},{"time":"152600","k200jisu":"1223.86","k200basis":"-0.01","tot3":2063002,"cha1":499137,"cha2":481444,"cha3":17692,"bcha1":12612199,"bcha2":10566889,"bcha3":2045309},{"time":"152700","k200jisu":"1223.86","k200basis":"0.84","tot3":2063002,"cha1":499137,"cha2":481444,"cha3":17692,"bcha1":12612199,"bcha2":10566889,"bcha3":2045309},{"time":"152800","k200jisu":"1223.86","k200basis":"1.29","tot3":2063002,"cha1":499137,"cha2":481444,"cha3":17692,"bcha1":12612199,"bcha2":10566889,"bcha3":2045309},{"time":"152900","k200jisu":"1223.86","k200basis":"1.59","tot3":2063002,"cha1":499137,"cha2":481444,"cha3":17692,"bcha1":12612199,"bcha2":10566889,"bcha3":2045309},{"time":"153000","k200jisu":"1224.78","k200basis":"1.62","tot3":2111350,"cha1":603337,"cha2":481447,"cha3":121890,"bcha1":13751636,"bcha2":11762176,"bcha3":1989460},{"time":"153200","k200jisu":"1225.23","k200basis":"-0.13","tot3":2051616,"cha1":620735,"cha2":481447,"cha3":139288,"bcha1":13928327,"bcha2":12016000,"bcha3":1912327},{"time":"154100","k200jisu":"1225.22","k200basis":"-1.22","tot3":2051520,"cha1":620783,"cha2":481447,"cha3":139336,"bcha1":13928456,"bcha2":12016272,"bcha3":1912184},{"time":"154600","k200jisu":"1225.22","k200basis":"2.78","tot3":2045595,"cha1":620783,"cha2":481447,"cha3":139336,"bcha1":13928526,"bcha2":12022267,"bcha3":1906260},{"time":"155100","k200jisu":"1225.22","k200basis":"2.78","tot3":2045094,"cha1":620783,"cha2":481447,"cha3":139336,"bcha1":13928579,"bcha2":12022821,"bcha3":1905758},{"time":"155600","k200jisu":"1225.22","k200basis":"2.78","tot3":2044441,"cha1":620783,"cha2":481447,"cha3":139336,"bcha1":13928588,"bcha2":12023482,"bcha3":1905105},{"time":"160100","k200jisu":"1225.22","k200basis":"2.78","tot3":2042788,"cha1":620783,"cha2":481447,"cha3":139336,"bcha1":13928604,"bcha2":12025152,"bcha3":1903452},{"time":"160500","k200jisu":"1225.22","k200basis":"2.78","tot3":2042788,"cha1":620783,"cha2":481447,"cha3":139336,"bcha1":13928604,"bcha2":12025152,"bcha3":1903452},{"time":"161000","k200jisu":"1225.22","k200basis":"2.78","tot3":2042794,"cha1":620790,"cha2":481447,"cha3":139343,"bcha1":13928604,"bcha2":12025152,"bcha3":1903452}]}
        """;

    /// <summary>
    /// Parses the embedded t1633 capture (newest-first) into MarketDaily points.
    /// t1633 ships per-day amounts in 백만원; the chart shows 억원 (÷ 100). The
    /// <c>yyyy-MM-dd</c> date goes into <c>Time</c> as the view's x value.
    /// </summary>
    static IReadOnlyList<ProgramTradeChartBuilder.ProgramFlowPoint> ParseT1633Capture()
    {
        using JsonDocument doc = JsonDocument.Parse(RealT1633Capture);
        JsonElement rows = doc.RootElement.GetProperty("t1633OutBlock1");

        var points = new List<ProgramTradeChartBuilder.ProgramFlowPoint>(rows.GetArrayLength());
        foreach (JsonElement r in rows.EnumerateArray())
        {
            string d = r.GetProperty("date").GetString()!;
            points.Add(new ProgramTradeChartBuilder.ProgramFlowPoint(
                Time: $"{d[..4]}-{d.Substring(4, 2)}-{d.Substring(6, 2)}",
                Kospi200: double.Parse(r.GetProperty("jisu").GetString()!, CultureInfo.InvariantCulture),
                Basis: 0,
                Net: 0,
                Arbitrage: r.GetProperty("cha3").GetInt64() / 100.0,
                NonArbitrage: r.GetProperty("bcha3").GetInt64() / 100.0,
                MinuteNet: 0));
        }
        points.Reverse();   // LS ships newest-first
        return points;
    }

    /// <summary>
    /// A real KOSPI t1633 (기간별 프로그램매매 추이) response, the most recent ~40
    /// daily rows. <c>cha3</c> / <c>bcha3</c> are that day's 차익 / 비차익 net
    /// buying; <c>jisu</c> is the KOSPI200 close. Amounts are LS amount-basis
    /// (gubun1=0, 백만원); only the chart's fields are retained.
    /// </summary>
    const string RealT1633Capture = """
        {"rsp_cd":"00000","rsp_msg":"OK","t1633OutBlock1":[
        {"date":"20260522","jisu":"1225.2","cha3":27414,"bcha3":-1178160},
        {"date":"20260521","jisu":"1225.2","cha3":139343,"bcha3":1899322},
        {"date":"20260520","jisu":"1125.5","cha3":108030,"bcha3":-1106468},
        {"date":"20260519","jisu":"1132.4","cha3":237840,"bcha3":-4816972},
        {"date":"20260518","jisu":"1171.3","cha3":160331,"bcha3":-1814804},
        {"date":"20260515","jisu":"1162.3","cha3":95595,"bcha3":-4466390},
        {"date":"20260514","jisu":"1243.1","cha3":200244,"bcha3":3314},
        {"date":"20260513","jisu":"1220.1","cha3":17996,"bcha3":-1532707},
        {"date":"20260512","jisu":"1183.4","cha3":-39452,"bcha3":-3375038},
        {"date":"20260511","jisu":"1211.4","cha3":65463,"bcha3":-3938656},
        {"date":"20260508","jisu":"1151.1","cha3":39817,"bcha3":-3361265},
        {"date":"20260507","jisu":"1149.8","cha3":165884,"bcha3":-4890638},
        {"date":"20260506","jisu":"1129.6","cha3":-557650,"bcha3":371661},
        {"date":"20260504","jisu":"1049.6","cha3":130019,"bcha3":2500093},
        {"date":"20260430","jisu":"992.15","cha3":364528,"bcha3":-1481914},
        {"date":"20260429","jisu":"1006.5","cha3":-34067,"bcha3":362478},
        {"date":"20260428","jisu":"999.03","cha3":-13908,"bcha3":471190},
        {"date":"20260427","jisu":"995.33","cha3":-53905,"bcha3":705951},
        {"date":"20260424","jisu":"971.87","cha3":5603,"bcha3":-1406230},
        {"date":"20260423","jisu":"975.62","cha3":106645,"bcha3":231818},
        {"date":"20260422","jisu":"964.52","cha3":-5908,"bcha3":-504280},
        {"date":"20260421","jisu":"962.26","cha3":-22416,"bcha3":940208},
        {"date":"20260420","jisu":"935.75","cha3":-95038,"bcha3":-193729},
        {"date":"20260417","jisu":"931.41","cha3":-3088,"bcha3":-1710577},
        {"date":"20260416","jisu":"937.87","cha3":161427,"bcha3":135956},
        {"date":"20260415","jisu":"916.60","cha3":179048,"bcha3":154785},
        {"date":"20260414","jisu":"897.00","cha3":166800,"bcha3":617807},
        {"date":"20260413","jisu":"870.78","cha3":-126137,"bcha3":33009},
        {"date":"20260410","jisu":"878.78","cha3":69150,"bcha3":70890},
        {"date":"20260409","jisu":"865.75","cha3":-43186,"bcha3":-1391935},
        {"date":"20260408","jisu":"882.81","cha3":515641,"bcha3":1266887},
        {"date":"20260407","jisu":"821.10","cha3":-150576,"bcha3":318267},
        {"date":"20260406","jisu":"811.84","cha3":174869,"bcha3":-319728},
        {"date":"20260403","jisu":"798.32","cha3":-30507,"bcha3":903360},
        {"date":"20260402","jisu":"774.63","cha3":-116192,"bcha3":106832},
        {"date":"20260401","jisu":"813.84","cha3":544873,"bcha3":271183},
        {"date":"20260331","jisu":"744.57","cha3":331619,"bcha3":-2352319},
        {"date":"20260330","jisu":"780.32","cha3":209895,"bcha3":-514591},
        {"date":"20260327","jisu":"805.19","cha3":22996,"bcha3":-1939137},
        {"date":"20260326","jisu":"808.89","cha3":233874,"bcha3":-1859937}
        ]}
        """;
}
