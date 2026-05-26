using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Charting;
using RedoxNet.LsOpenApi.Core.Http;
using RedoxNet.LsOpenApi.Core.Indicators;
using RedoxNet.LsOpenApi.Core.Models;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tools for LS overseas stock market-data TRs (g31xx/g32xx).
/// </summary>
/// <remarks>
/// US-focused: the search tool defaults to natcode='US' and the chart/quote
/// tools default to exchcd='82' (Nasdaq). Non-US queries require passing a
/// numeric exchange code and the appropriate natcode through ls_search_overseas_stock.
/// </remarks>
[McpServerToolType]
public static class OverseasStockTools
{
    const int MaxFetchCount = 500;
    // g3190 rate_limit_per_sec = 1; at most 10 pages × 500 = 5,000 master rows
    // scanned, which covers full Nasdaq alphabetically. Common ticker queries
    // short-circuit via the g3104 fast path below, so this only bites on
    // name-only searches (e.g. "NVIDIA", "엔비디아").
    const int SearchMaxPages = 10;
    // Ranking weights for master-scan matches. Exact ticker / korname matches
    // outrank ETFs whose names merely contain the same substring.
    const int ScoreExactSymbol = 1000;
    const int ScoreExactKorname = 800;
    const int ScoreEngnameStarts = 400;
    const int ScoreSymbolStarts = 200;
    const int ScoreSubstring = 50;

    static readonly IndicatorService Indicators = new();
    static readonly HashSet<string> KnownPeriodTypes =
        new(StringComparer.OrdinalIgnoreCase) { "day", "week", "month", "year", "min", "tick" };
    static readonly HashSet<string> KnownOutputModes =
        new(StringComparer.OrdinalIgnoreCase) { "display", "analyze", "export", "reference" };

    // ============================================================
    // Tool: ls_search_overseas_stock
    // ============================================================

    /// <summary>
    /// Searches the overseas stock master and returns key symbols for later quote/chart calls.
    /// </summary>
    [McpServerTool(Name = "ls_search_overseas_stock")]
    [Description("""
        Searches the LS overseas stock master (g3190) and returns symbols plus keysymbol/exchcd for quote and chart calls.

        USE WHEN: the user names a US stock but only knows the ticker or Korean/English name, e.g. "테슬라", "Tesla", "NVDA", "미장 엔비디아".
        AVOID WHEN: the user already supplied keysymbol/exchcd/symbol; call ls_get_overseas_quote or ls_get_overseas_chart directly.

        Best results: pass the US ticker when you know it (e.g. "NVDA" for 엔비디아). A ticker keyword routes through a fast direct lookup, while a Korean/English name falls back to a paginated master scan that is slower and may miss symbols deep in the alphabetical listing.

        US exchange defaults: exchange='nasdaq' maps to exgubun=2/exchcd=82, exchange='nyse' or 'amex' maps to exgubun=1/exchcd=81, exchange='all' scans both with exgubun=0. natcode defaults to 'US'; non-US queries require passing natcode and exchange='all' (or a numeric exgubun).
        """)]
    public static async Task<string> SearchOverseasStock(
        LsApiClient apiClient,
        [Description("Ticker or Korean/English name fragment, e.g. 'TSLA', '테슬라', 'Tesla'.")]
        string keyword = "",
        [Description("Exchange filter: 'all' (default), 'nasdaq', 'nyse', 'amex', '82', or '81'.")]
        string exchange = "all",
        [Description("Country code. Default 'US'.")]
        string natcode = "US",
        [Description("Max results (1-100). Default 20.")]
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return McpJson.Error("keyword is required.");

        int cappedLimit = Math.Clamp(limit, 1, 100);
        string exgubun = NormalizeExchangeGroup(exchange);
        string needle = keyword.Trim();
        string needleUpper = needle.ToUpperInvariant();
        string resolvedNatcode = string.IsNullOrWhiteSpace(natcode) ? "US" : natcode.Trim().ToUpperInvariant();

        try
        {
            // (1) Ticker fast path. The master (g3190) is paginated alphabetically;
            // tickers like NVIDIA / NVDA sit far enough into the listing that a
            // 5-page scan misses them, and ETF names containing "NVDA" otherwise
            // outrank the real ticker by sheer alphabetical luck. A direct
            // g3104 probe by keysymbol resolves both problems in one call.
            var directHits = new List<DiscoveredSymbol>();
            bool g3104Used = false;
            if (LooksLikeTicker(needleUpper) && resolvedNatcode == "US")
            {
                string[] probeExchanges = exgubun switch
                {
                    "2" => new[] { "82" },
                    "1" => new[] { "81" },
                    _ => new[] { "82", "81" },
                };
                foreach (string ex in probeExchanges)
                {
                    g3104Used = true;
                    DiscoveredSymbol? hit = await ProbeOverseasTickerAsync(
                        apiClient, ex, needleUpper, cancellationToken).ConfigureAwait(false);
                    if (hit is not null)
                        directHits.Add(hit);
                }
            }

            // (2) Master scan with relevance scoring. Accumulate beyond
            // cappedLimit so the ranker has options — a high-scoring exact
            // match on page 6 must beat low-scoring substring noise on page 1.
            var scored = new List<(int Score, DiscoveredSymbol Row)>();
            int scanned = 0;
            string cursor = "";
            int pages = 0;
            int scanTarget = Math.Max(cappedLimit * 3, cappedLimit + 20);

            do
            {
                // g3190 needs `tr_cont: Y` (set by passing continuationKey)
                // to recognize a paginated request, even though it's a
                // body-cursor TR. Without the header LS silently resets
                // every request to page 1 — same `cts_value` in both
                // request and response, indistinguishable from a stuck
                // cursor. Passing the cursor as continuationKey sets both
                // the header and `tr_cont_key`; the body `cts_value` is
                // redundant but kept for catalog conformance.
                LsTrResponse response = await apiClient.CallTrAsync(
                    "g3190",
                    new JsonObject
                    {
                        ["delaygb"] = "R",
                        ["natcode"] = resolvedNatcode,
                        ["exgubun"] = exgubun,
                        ["readcnt"] = 500,
                        ["cts_value"] = cursor,
                    },
                    continuationKey: string.IsNullOrWhiteSpace(cursor) ? null : cursor,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccess)
                    return McpJson.Error("LS reported a business-level error.", new { rsp_cd = response.RspCode, rsp_msg = response.RspMessage });

                JsonElement? rows = response.GetBlock("g3190OutBlock1");
                if (rows is { ValueKind: JsonValueKind.Array })
                {
                    foreach (JsonElement row in rows.Value.EnumerateArray())
                    {
                        string? symbol = row.ReadString("symbol");
                        string? keySymbol = row.ReadString("keysymbol");
                        string? korName = row.ReadString("korname");
                        string? engName = row.ReadString("engname");
                        if (!ContainsAny(symbol, keySymbol, korName, engName, needle))
                            continue;

                        scanned++;
                        int score = ScoreRelevance(symbol, keySymbol, korName, engName, needle, needleUpper);
                        scored.Add((score, new DiscoveredSymbol(
                            KeySymbol: keySymbol,
                            Symbol: symbol,
                            Exchcd: row.ReadString("exchcd"),
                            Exchange: ExchangeName(row.ReadString("exchcd")),
                            Natcode: row.ReadString("natcode"),
                            KoreanName: korName,
                            EnglishName: engName,
                            Currency: row.ReadString("currency"),
                            Isin: row.ReadString("isin"),
                            ListedDate: row.ReadString("listed_date"),
                            Suspend: row.ReadString("suspend"),
                            Sellonly: row.ReadString("sellonly"),
                            FractionalTrading: row.ReadString("point"))));
                    }
                }

                cursor = response.ContinuationKeys.TryGetValue("cts_value", out string? nextCursor) ? nextCursor : "";
                pages++;
            }
            while (scored.Count < scanTarget && !string.IsNullOrWhiteSpace(cursor) && pages < SearchMaxPages);

            // (3) Combine: direct hits first (always exact ticker matches),
            // then the master-scan ranking. Dedupe on keysymbol so a direct
            // hit doesn't appear twice if the master scan also surfaced it.
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<DiscoveredSymbol>();
            foreach (DiscoveredSymbol hit in directHits)
            {
                if (results.Count >= cappedLimit) break;
                if (!string.IsNullOrWhiteSpace(hit.KeySymbol) && seenKeys.Add(hit.KeySymbol!))
                    results.Add(hit);
            }
            foreach ((_, DiscoveredSymbol row) in scored.OrderByDescending(s => s.Score))
            {
                if (results.Count >= cappedLimit) break;
                if (!string.IsNullOrWhiteSpace(row.KeySymbol) && seenKeys.Add(row.KeySymbol!))
                    results.Add(row);
            }

            return JsonSerializer.Serialize(new
            {
                keyword = needle,
                exchange_filter = exchange,
                country = resolvedNatcode,
                count = results.Count,
                matches_scanned = scanned,
                pages_scanned = pages,
                results = results.Select(r => new
                {
                    keysymbol = r.KeySymbol,
                    symbol = r.Symbol,
                    exchcd = r.Exchcd,
                    exchange = r.Exchange,
                    natcode = r.Natcode,
                    korean_name = r.KoreanName,
                    english_name = r.EnglishName,
                    currency = r.Currency,
                    isin = r.Isin,
                    listed_date = r.ListedDate,
                    suspend = r.Suspend,
                    sellonly = r.Sellonly,
                    fractional_trading = r.FractionalTrading,
                }),
                source_tr = g3104Used && directHits.Count > 0 ? "g3104+g3190" : "g3190",
            }, McpJson.Tool);
        }
        catch (LsAuthException ex)
        {
            return McpJson.Error("Authentication failed.", new { reason = ex.Message });
        }
        catch (LsTrException ex)
        {
            return McpJson.Error("TR call failed.", new { reason = ex.Message, status = ex.StatusCode });
        }
    }

    static bool LooksLikeTicker(string upper)
    {
        if (upper.Length is < 1 or > 5) return false;
        foreach (char c in upper)
        {
            // US tickers: A-Z and 0-9. Dotted tickers (BRK.B) are 4-5 chars
            // including the dot — accept those too.
            if (!(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '.'))
                return false;
        }
        return true;
    }

    static async Task<DiscoveredSymbol?> ProbeOverseasTickerAsync(
        LsApiClient apiClient,
        string exchcd,
        string symbolUpper,
        CancellationToken ct)
    {
        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "g3104",
                new JsonObject
                {
                    ["delaygb"] = "R",
                    ["keysymbol"] = exchcd + symbolUpper,
                    ["exchcd"] = exchcd,
                    ["symbol"] = symbolUpper,
                },
                cancellationToken: ct).ConfigureAwait(false);

            if (!response.IsSuccess)
                return null;

            JsonElement? block = response.GetBlock("g3104OutBlock");
            if (block is null) return null;

            JsonElement b = block.Value;
            string? korname = b.ReadString("korname");
            string? engname = b.ReadString("engname");
            // LS sometimes returns 00000 with an empty body for unknown
            // symbols. Treat empty korname AND empty engname as "no hit".
            if (string.IsNullOrWhiteSpace(korname) && string.IsNullOrWhiteSpace(engname))
                return null;

            return new DiscoveredSymbol(
                KeySymbol: b.ReadString("keysymbol") ?? (exchcd + symbolUpper),
                Symbol: b.ReadString("symbol") ?? symbolUpper,
                Exchcd: b.ReadString("exchcd") ?? exchcd,
                Exchange: ExchangeName(b.ReadString("exchcd") ?? exchcd),
                Natcode: null,
                KoreanName: korname,
                EnglishName: engname,
                Currency: b.ReadString("currency"),
                Isin: null,
                ListedDate: null,
                Suspend: b.ReadString("suspend"),
                Sellonly: b.ReadString("sellonly"),
                FractionalTrading: null);
        }
        catch (LsTrException)
        {
            return null;
        }
    }

    static int ScoreRelevance(
        string? symbol,
        string? keysymbol,
        string? korname,
        string? engname,
        string needle,
        string needleUpper)
    {
        int score = 0;
        if (string.Equals(symbol, needleUpper, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(keysymbol, needleUpper, StringComparison.OrdinalIgnoreCase))
            score += ScoreExactSymbol;
        if (!string.IsNullOrWhiteSpace(korname) && string.Equals(korname, needle, StringComparison.Ordinal))
            score += ScoreExactKorname;
        if (!string.IsNullOrWhiteSpace(engname) &&
            (string.Equals(engname, needleUpper, StringComparison.OrdinalIgnoreCase) ||
             engname.StartsWith(needleUpper + " ", StringComparison.OrdinalIgnoreCase)))
            score += ScoreEngnameStarts;
        if (!string.IsNullOrWhiteSpace(symbol) &&
            symbol.StartsWith(needleUpper, StringComparison.OrdinalIgnoreCase))
            score += ScoreSymbolStarts;
        // Any substring hit (the ContainsAny gate) is worth a baseline so
        // matches still sort above unmatched rows when no stronger signal fires.
        score += ScoreSubstring;
        return score;
    }

    // ============================================================
    // Tool: ls_get_overseas_quote
    // ============================================================

    /// <summary>
    /// Returns a quote snapshot for a single overseas stock.
    /// </summary>
    [McpServerTool(Name = "ls_get_overseas_quote")]
    [Description("""
        Returns a quote snapshot for a single overseas stock via LS g3101, optionally adding profile/fundamental fields (g3104) and 10-level order book (g3106).

        USE WHEN: the user asks for a US/overseas stock price, "미장 TSLA 현재가", "엔비디아 호가", or overseas stock snapshot.
        AVOID WHEN: the user wants Korean stocks — use ls_get_quote. For OHLCV history use ls_get_overseas_chart.

        Pass keysymbol from ls_search_overseas_stock when available. Without keysymbol, exchcd + symbol is used (default exchcd='82' for Nasdaq). The timestamp field is the Seoul wall-clock when the call was made (matches the LS broker's view), not the US local-market time.
        """)]
    public static async Task<string> GetOverseasQuote(
        LsApiClient apiClient,
        [Description("Ticker symbol, e.g. 'TSLA'. If keysymbol is supplied this may be omitted.")]
        string symbol = "",
        [Description("Exchange code: '82' Nasdaq (default) or '81' NYSE/AMEX. Aliases nasdaq/nyse/amex accepted.")]
        string exchcd = "82",
        [Description("LS key symbol such as '82TSLA'. Preferred when known.")]
        string? keysymbol = null,
        [Description("If true, add company/security metadata from g3104.")]
        bool include_profile = false,
        [Description("If true, add 10-level order book from g3106.")]
        bool include_orderbook = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveOverseasSymbol(symbol, exchcd, keysymbol, out OverseasSymbol resolved, out string? error))
            return McpJson.Error(error ?? "Invalid overseas symbol.");

        try
        {
            LsTrResponse quoteResponse = await apiClient.CallTrAsync(
                "g3101",
                BuildSymbolInBlock("g3101InBlock", resolved),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!quoteResponse.IsSuccess)
                return BusinessError(quoteResponse, "g3101", resolved);

            JsonElement? quoteBlock = quoteResponse.GetBlock("g3101OutBlock");
            if (quoteBlock is null)
                return McpJson.Error("g3101OutBlock was missing from the response.", resolved);

            JsonElement q = quoteBlock.Value;
            object? profile = null;
            if (include_profile)
            {
                LsTrResponse profileResponse = await apiClient.CallTrAsync(
                    "g3104",
                    BuildSymbolInBlock("g3104InBlock", resolved),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!profileResponse.IsSuccess)
                    return BusinessError(profileResponse, "g3104", resolved);
                if (profileResponse.GetBlock("g3104OutBlock") is { } p)
                    profile = BuildProfile(p);
            }

            object? orderBook = null;
            if (include_orderbook)
            {
                LsTrResponse orderResponse = await apiClient.CallTrAsync(
                    "g3106",
                    BuildSymbolInBlock("g3106InBlock", resolved),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!orderResponse.IsSuccess)
                    return BusinessError(orderResponse, "g3106", resolved);
                if (orderResponse.GetBlock("g3106OutBlock") is { } ob)
                    orderBook = BuildOrderBook(ob);
            }

            string resolvedExch = q.ReadString("exchcd") ?? resolved.Exchcd;
            var payload = new
            {
                keysymbol = q.ReadString("keysymbol") ?? resolved.KeySymbol,
                symbol = q.ReadString("symbol") ?? resolved.Symbol,
                exchcd = resolvedExch,
                exchange = ExchangeName(resolvedExch),
                exchange_id = q.ReadString("exchange"),
                name = q.ReadString("korname"),
                industry = q.ReadString("induname"),
                trading_status = q.ReadString("suspend"),
                sellonly = q.ReadString("sellonly"),
                currency = q.ReadString("currency") ?? CurrencyHint(resolvedExch),
                price = q.ReadDecimal("price"),
                sign = q.ReadString("sign"),
                change = Signed(q.ReadDouble("diff"), q.ReadString("sign")),
                change_percent = Signed(q.ReadDouble("rate"), q.ReadString("sign")),
                volume = q.ReadLong("volume"),
                amount = q.ReadLong("amount"),
                open = q.ReadDecimal("open"),
                high = q.ReadDecimal("high"),
                low = q.ReadDecimal("low"),
                high_52w = q.ReadDecimal("high52p"),
                low_52w = q.ReadDecimal("low52p"),
                upper_limit_price = q.ReadDecimal("uplimit"),
                lower_limit_price = q.ReadDecimal("dnlimit"),
                per = q.ReadDouble("perv"),
                eps = q.ReadDouble("epsv"),
                floatpoint = q.ReadString("floatpoint"),
                profile,
                order_book = orderBook,
                source_trs = new[] { "g3101" }
                    .Concat(include_profile ? new[] { "g3104" } : Array.Empty<string>())
                    .Concat(include_orderbook ? new[] { "g3106" } : Array.Empty<string>())
                    .ToArray(),
                timestamp = GetIndexQuoteTool.SeoulNowIsoString(),
                timestamp_tz = "Asia/Seoul",
            };

            return JsonSerializer.Serialize(payload, McpJson.Tool);
        }
        catch (LsAuthException ex)
        {
            return McpJson.Error("Authentication failed.", new { reason = ex.Message });
        }
        catch (LsTrException ex)
        {
            return McpJson.Error("TR call failed.", new { reason = ex.Message, status = ex.StatusCode });
        }
    }

    // ============================================================
    // Tool: ls_get_overseas_chart
    // ============================================================

    /// <summary>
    /// Returns OHLCV candles for a single overseas stock.
    /// </summary>
    [McpServerTool(Name = "ls_get_overseas_chart")]
    [Description("""
        Returns OHLCV candles for an overseas stock via LS g3204 (day/week/month/year), g3203 (minute), or g3202 (tick), with optional indicators and Plotly chart rendering.

        USE WHEN: the user asks for a US/overseas stock chart, 미장 일봉/주봉/월봉/분봉/틱, OHLCV, or technical-analysis context for a stock such as TSLA/NVDA/AAPL.
        AVOID WHEN: the user wants Korean stock candles — use ls_get_chart. For current price only use ls_get_overseas_quote.

        output_mode controls token cost:
        - display: summary text + structuredContent.chart when include_chart=true.
        - analyze: summary + compact context, no raw candles.
        - export: raw OHLCV and indicator arrays.
        - reference: dataset_id and metadata only.

        The returned dataset_id is accepted by ls_add_indicator and ls_reframe_chart for follow-up calls like "MA200도 추가" or "일봉으로 바꿔서 6개월" — KR and US datasets share the same handle cache.

        Date/time semantics: daily/weekly/monthly/yearly bars are indexed by the US local trading day (ET); minute/tick bars carry US ET timestamps via LS's loctime field. The response's bar_timezone field surfaces this ('America/New_York' for Nasdaq/NYSE/AMEX) so a "5/22 일봉" can be read as "the NYSE 5/22 session", which lands ~14 hours behind Asia/Seoul during DST.
        """)]
    public static async Task<CallToolResult> GetOverseasChart(
        LsApiClient apiClient,
        [Description("Ticker symbol, e.g. 'TSLA'. If keysymbol is supplied this may be omitted.")]
        string symbol = "",
        [Description("Period type: 'day', 'week', 'month', 'year', 'min', or 'tick'.")]
        string period_type = "day",
        [Description("Number of candles (1-500). Default 60.")]
        int count = 60,
        [Description("Exchange code: '82' Nasdaq (default) or '81' NYSE/AMEX. Aliases nasdaq/nyse/amex accepted.")]
        string exchcd = "82",
        [Description("LS key symbol such as '82TSLA'. Preferred when known.")]
        string? keysymbol = null,
        [Description("Optional start date in yyyyMMdd format.")]
        string? from = null,
        [Description("Optional end date in yyyyMMdd format.")]
        string? to = null,
        [Description("For period_type='min': minute interval. For 'tick': tick grouping. Default 5.")]
        int unit = 5,
        [Description("Optional indicator specs, e.g. ['ma:20','rsi:14','macd:12,26,9','bb:20,2'].")]
        string[]? indicators = null,
        [Description("If true, ship a Plotly v5 candlestick chart as structuredContent.")]
        bool include_chart = false,
        [Description("Output intent: display, analyze, export, or reference. Defaults to display when include_chart=true, otherwise analyze.")]
        string? output_mode = null,
        [Description("Optional human-readable stock name for the chart title.")]
        string? name = null,
        [Description("For day/week/month/year charts, apply adjusted prices. Default true.")]
        bool adjusted = true,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveOverseasSymbol(symbol, exchcd, keysymbol, out OverseasSymbol resolved, out string? error))
            return McpJson.ErrorResult(error ?? "Invalid overseas symbol.");

        string period = period_type.Trim().ToLowerInvariant();
        if (!KnownPeriodTypes.Contains(period))
            return McpJson.ErrorResult($"Unknown period_type '{period_type}'. Use day, week, month, year, min, or tick.");

        string outputMode = ResolveOutputMode(output_mode, include_chart);
        if (!KnownOutputModes.Contains(outputMode))
            return McpJson.ErrorResult($"Unknown output_mode '{output_mode}'. Use display, analyze, export, or reference.");

        var specs = new List<IndicatorSpec>();
        if (indicators is { Length: > 0 })
        {
            foreach (string raw in indicators)
            {
                if (!IndicatorSpecParser.TryParse(raw, out IndicatorSpec? spec, out string? parseError))
                    return McpJson.ErrorResult($"Invalid indicator spec '{raw}'.", new { reason = parseError });
                specs.Add(spec!);
            }
        }

        try
        {
            int cappedDisplay = Math.Clamp(count, 1, MaxFetchCount);
            OverseasFrame frame = await FetchOverseasFrameAsync(
                apiClient, resolved, name, period, cappedDisplay, from, to, unit, specs, adjusted, withWarmup: null, cancellationToken)
                .ConfigureAwait(false);

            string datasetId = DatasetHandleCache.Add("overseas_chart", new OverseasChartDataset(
                resolved,
                name,
                frame.PeriodType,
                frame.TrCode,
                unit,
                from,
                to,
                frame.Candles,
                frame.Indicators,
                specs,
                frame.Context,
                frame.Summary,
                adjusted,
                DateTimeOffset.UtcNow));

            string? currency = CurrencyHint(resolved.Exchcd);
            string? barTimezone = BarTimezone(resolved.Exchcd);
            object textPayload = outputMode switch
            {
                "export" => new
                {
                    symbol = resolved.Symbol,
                    exchcd = resolved.Exchcd,
                    keysymbol = resolved.KeySymbol,
                    currency,
                    bar_timezone = barTimezone,
                    period_type = frame.PeriodType,
                    output_mode = outputMode,
                    dataset_id = datasetId,
                    tr_cd = frame.TrCode,
                    count = frame.Candles.Count,
                    summary = frame.Summary,
                    candles = SerializeCandles(frame.Candles, frame.PeriodType),
                    indicators = frame.Indicators,
                    context = frame.Context,
                },
                "reference" => new
                {
                    symbol = resolved.Symbol,
                    exchcd = resolved.Exchcd,
                    keysymbol = resolved.KeySymbol,
                    currency,
                    bar_timezone = barTimezone,
                    period_type = frame.PeriodType,
                    output_mode = outputMode,
                    dataset_id = datasetId,
                    tr_cd = frame.TrCode,
                    count = frame.Candles.Count,
                    range = frame.Summary.DateRange,
                },
                _ => new
                {
                    symbol = resolved.Symbol,
                    exchcd = resolved.Exchcd,
                    keysymbol = resolved.KeySymbol,
                    currency,
                    bar_timezone = barTimezone,
                    period_type = frame.PeriodType,
                    output_mode = outputMode,
                    dataset_id = datasetId,
                    tr_cd = frame.TrCode,
                    count = frame.Candles.Count,
                    summary = frame.Summary,
                    context = frame.Context,
                },
            };

            JsonObject? structured = null;
            if (include_chart || outputMode == "display")
            {
                structured = new JsonObject
                {
                    ["chart"] = CandlestickChartBuilder.Build(
                        resolved.Symbol,
                        frame.PeriodType,
                        frame.Candles,
                        frame.Indicators,
                        specs,
                        name,
                        currency),
                };
            }

            return McpJson.OkResult(JsonSerializer.Serialize(textPayload, McpJson.Tool), structured);
        }
        catch (LsAuthException ex)
        {
            return McpJson.ErrorResult("Authentication failed.", new { reason = ex.Message });
        }
        catch (LsTrException ex)
        {
            return McpJson.ErrorResult("TR call failed.", new { reason = ex.Message, status = ex.StatusCode });
        }
    }

    // ============================================================
    // Internal follow-up routes for ls_add_indicator / ls_reframe_chart
    // ============================================================

    /// <summary>
    /// Routes <c>ls_add_indicator</c> for an overseas chart dataset. Returns
    /// <see langword="null"/> when the handle does not belong to an overseas
    /// dataset so the Korean (<see cref="GetChartTool"/>) path can try it.
    /// </summary>
    internal static async Task<CallToolResult?> TryAddIndicatorAsync(
        LsApiClient apiClient,
        string datasetId,
        IndicatorSpec specToAdd,
        bool includeChart,
        CancellationToken cancellationToken)
    {
        if (!DatasetHandleCache.TryGet<OverseasChartDataset>(datasetId, out OverseasChartDataset? dataset) || dataset is null)
            return null;

        List<IndicatorSpec> specs = MergeIndicatorSpecs(dataset.IndicatorSpecs, specToAdd);
        try
        {
            OverseasFrame refreshed = await FetchOverseasFrameAsync(
                apiClient,
                dataset.Symbol,
                dataset.Name,
                dataset.PeriodType,
                dataset.Candles.Count,
                dataset.SourceFrom,
                dataset.SourceTo,
                dataset.Unit,
                specs,
                dataset.Adjusted,
                withWarmup: null,
                cancellationToken).ConfigureAwait(false);

            OverseasChartDataset updated = dataset with
            {
                Candles = refreshed.Candles,
                Indicators = refreshed.Indicators,
                IndicatorSpecs = specs,
                Context = refreshed.Context,
                Summary = refreshed.Summary,
            };
            if (!DatasetHandleCache.TryUpdate(datasetId, updated))
                return McpJson.ErrorResult("Unknown or expired dataset_id.", new { dataset_id = datasetId });

            var payload = new
            {
                symbol = dataset.Symbol.Symbol,
                keysymbol = dataset.Symbol.KeySymbol,
                exchcd = dataset.Symbol.Exchcd,
                currency = CurrencyHint(dataset.Symbol.Exchcd),
                bar_timezone = BarTimezone(dataset.Symbol.Exchcd),
                period_type = updated.PeriodType,
                dataset_id = datasetId,
                added_indicator = specToAdd.Raw,
                indicator = SummarizeIndicator(specToAdd.Raw, refreshed.Indicators),
                summary = refreshed.Summary,
                context = refreshed.Context,
            };

            JsonObject? structured = includeChart
                ? new JsonObject
                {
                    ["chart"] = CandlestickChartBuilder.Build(
                        dataset.Symbol.Symbol,
                        updated.PeriodType,
                        updated.Candles,
                        updated.Indicators,
                        specs,
                        dataset.Name,
                        CurrencyHint(dataset.Symbol.Exchcd)),
                }
                : null;

            return McpJson.OkResult(JsonSerializer.Serialize(payload, McpJson.Tool), structured);
        }
        catch (LsAuthException ex)
        {
            return McpJson.ErrorResult("Authentication failed.", new { reason = ex.Message });
        }
        catch (LsTrException ex)
        {
            return McpJson.ErrorResult("TR call failed.", new { reason = ex.Message, status = ex.StatusCode });
        }
    }

    /// <summary>
    /// Routes <c>ls_reframe_chart</c> for an overseas chart dataset. Returns
    /// <see langword="null"/> when the handle does not belong to an overseas
    /// dataset so the Korean path can try it.
    /// </summary>
    internal static async Task<CallToolResult?> TryReframeAsync(
        LsApiClient apiClient,
        string datasetId,
        string periodType,
        int count,
        string? from,
        string? to,
        int unit,
        bool includeChart,
        CancellationToken cancellationToken)
    {
        if (!DatasetHandleCache.TryGet<OverseasChartDataset>(datasetId, out OverseasChartDataset? dataset) || dataset is null)
            return null;

        string period = periodType.Trim().ToLowerInvariant();
        if (!KnownPeriodTypes.Contains(period))
            return McpJson.ErrorResult($"Unknown period_type '{periodType}'. Use day, week, month, year, min, or tick.");

        try
        {
            int cappedDisplay = Math.Clamp(count, 1, MaxFetchCount);
            OverseasFrame refreshed = await FetchOverseasFrameAsync(
                apiClient,
                dataset.Symbol,
                dataset.Name,
                period,
                cappedDisplay,
                from,
                to,
                unit,
                dataset.IndicatorSpecs,
                dataset.Adjusted,
                withWarmup: null,
                cancellationToken).ConfigureAwait(false);

            OverseasChartDataset updated = dataset with
            {
                PeriodType = refreshed.PeriodType,
                TrCode = refreshed.TrCode,
                Unit = unit,
                SourceFrom = from,
                SourceTo = to,
                Candles = refreshed.Candles,
                Indicators = refreshed.Indicators,
                Context = refreshed.Context,
                Summary = refreshed.Summary,
            };
            if (!DatasetHandleCache.TryUpdate(datasetId, updated))
                return McpJson.ErrorResult("Unknown or expired dataset_id.", new { dataset_id = datasetId });

            var payload = new
            {
                symbol = dataset.Symbol.Symbol,
                keysymbol = dataset.Symbol.KeySymbol,
                exchcd = dataset.Symbol.Exchcd,
                currency = CurrencyHint(dataset.Symbol.Exchcd),
                bar_timezone = BarTimezone(dataset.Symbol.Exchcd),
                period_type = updated.PeriodType,
                dataset_id = datasetId,
                tr_cd = refreshed.TrCode,
                count = refreshed.Candles.Count,
                summary = refreshed.Summary,
                context = refreshed.Context,
            };

            JsonObject? structured = includeChart
                ? new JsonObject
                {
                    ["chart"] = CandlestickChartBuilder.Build(
                        dataset.Symbol.Symbol,
                        refreshed.PeriodType,
                        refreshed.Candles,
                        refreshed.Indicators,
                        dataset.IndicatorSpecs,
                        dataset.Name,
                        CurrencyHint(dataset.Symbol.Exchcd)),
                }
                : null;

            return McpJson.OkResult(JsonSerializer.Serialize(payload, McpJson.Tool), structured);
        }
        catch (LsAuthException ex)
        {
            return McpJson.ErrorResult("Authentication failed.", new { reason = ex.Message });
        }
        catch (LsTrException ex)
        {
            return McpJson.ErrorResult("TR call failed.", new { reason = ex.Message, status = ex.StatusCode });
        }
    }

    // ============================================================
    // Fetch helpers (private)
    // ============================================================

    static async Task<OverseasFrame> FetchOverseasFrameAsync(
        LsApiClient apiClient,
        OverseasSymbol symbol,
        string? name,
        string period,
        int count,
        string? from,
        string? to,
        int unit,
        IReadOnlyList<IndicatorSpec> specs,
        bool adjusted,
        bool? withWarmup,
        CancellationToken ct)
    {
        // Same warm-up policy as GetChartTool:
        //   apply summary warm-up iff `from` is unspecified — an explicit `from`
        //   is a deliberate narrow window we should not silently widen.
        //   `withWarmup` overrides the default (force-on or force-off).
        bool applyWarmup = withWarmup ?? string.IsNullOrWhiteSpace(from);
        int indicatorWarmup = RequiredWarmup(specs);
        int summaryWarmup = applyWarmup ? SummaryWarmup(period) : 0;
        int warmup = Math.Max(indicatorWarmup, summaryWarmup);
        int fetchCount = Math.Min(count + warmup, MaxFetchCount);
        int effectiveWarmup = Math.Max(0, fetchCount - count);

        (List<Candle> candles, string trCode) = period switch
        {
            "day" => (await FetchOverseasDailyAsync(apiClient, symbol, "2", fetchCount, effectiveWarmup, from, to, adjusted, ct).ConfigureAwait(false), "g3204"),
            "week" => (await FetchOverseasDailyAsync(apiClient, symbol, "3", fetchCount, effectiveWarmup, from, to, adjusted, ct).ConfigureAwait(false), "g3204"),
            "month" => (await FetchOverseasDailyAsync(apiClient, symbol, "4", fetchCount, effectiveWarmup, from, to, adjusted, ct).ConfigureAwait(false), "g3204"),
            "year" => (await FetchOverseasDailyAsync(apiClient, symbol, "5", fetchCount, effectiveWarmup, from, to, adjusted, ct).ConfigureAwait(false), "g3204"),
            "min" => (await FetchOverseasMinuteAsync(apiClient, symbol, Math.Max(1, unit), fetchCount, from, to, ct).ConfigureAwait(false), "g3203"),
            "tick" => (await FetchOverseasTickAsync(apiClient, symbol, Math.Max(1, unit), fetchCount, from, to, ct).ConfigureAwait(false), "g3202"),
            _ => throw new InvalidOperationException($"Unknown period '{period}'."),
        };

        IReadOnlyDictionary<string, IReadOnlyList<double?>> indicatorResults =
            specs.Count == 0
                ? new Dictionary<string, IReadOnlyList<double?>>()
                : Indicators.Compute(candles, specs);

        // Build the summary over the full warm-up-inclusive series before the
        // display trim, so its long-period MAs / slope / 1Y change populate
        // regardless of how short `count` is.
        AnalyticalSummary summary = AnalyticalSummaryBuilder.Build(
            symbol.Symbol,
            name,
            period,
            candles,
            displayBarCount: Math.Min(candles.Count, count),
            warmupApplied: applyWarmup);

        // Trim the warm-up lead. Indicators were computed over the full series,
        // so the trimmed series stay fully populated across the display window.
        if (candles.Count > count)
        {
            int drop = candles.Count - count;
            candles = candles.GetRange(drop, count);
            if (indicatorResults.Count > 0)
            {
                indicatorResults = indicatorResults.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<double?>)kv.Value.Skip(drop).ToList(),
                    StringComparer.Ordinal);
            }
        }

        ChartContext context = ChartContextBuilder.Build(candles, indicatorResults, specs);
        return new OverseasFrame(period, trCode, candles, indicatorResults, context, summary);
    }

    static async Task<List<Candle>> FetchOverseasDailyAsync(
        LsApiClient apiClient,
        OverseasSymbol symbol,
        string gubun,
        int count,
        int warmup,
        string? from,
        string? to,
        bool adjusted,
        CancellationToken ct)
    {
        DateTime today = DateTime.Today;
        string effectiveEnd = !string.IsNullOrWhiteSpace(to)
            ? to!
            : today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        string effectiveStart;
        if (!string.IsNullOrWhiteSpace(from))
        {
            // Push the explicit start back by the warm-up window so indicators
            // are populated from the first displayed candle. warmup==0 leaves
            // `from` exactly as the caller gave it.
            effectiveStart = warmup > 0
                && DateTime.TryParseExact(from, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime fromDate)
                ? fromDate.AddDays(-CalendarDaysBack(gubun, warmup)).ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                : from!;
        }
        else
        {
            effectiveStart = today.AddDays(-CalendarDaysBack(gubun, count)).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        LsTrResponse response = await apiClient.CallTrAsync(
            "g3204",
            new JsonObject
            {
                ["sujung"] = adjusted ? "Y" : "N",
                ["delaygb"] = "R",
                ["keysymbol"] = symbol.KeySymbol,
                ["exchcd"] = symbol.Exchcd,
                ["symbol"] = symbol.Symbol,
                ["gubun"] = gubun,
                ["qrycnt"] = count,
                // comp_yn = 압축여부 (Y=압축, N=비압축). Forced to "N":
                // asking LS for the compressed path corrupts the price
                // fields on overseas charts (rsp_cd=IGW40014, e.g.
                // "id=[시가(open)] in.data=[ 190.8400(<] dPoint=[8]"
                // — control bytes prefixing what should be a USD price).
                // KR chart TRs use "N" for the same reason.
                ["comp_yn"] = "N",
                ["sdate"] = effectiveStart,
                ["edate"] = effectiveEnd,
                ["cts_date"] = "",
                ["cts_info"] = "",
            },
            cancellationToken: ct).ConfigureAwait(false);
        EnsureSuccess(response);
        return ParseOverseasCandles(response.GetBlock("g3204OutBlock1"), periodType: "day", max: count);
    }

    static async Task<List<Candle>> FetchOverseasMinuteAsync(
        LsApiClient apiClient,
        OverseasSymbol symbol,
        int unit,
        int count,
        string? from,
        string? to,
        CancellationToken ct)
    {
        LsTrResponse response = await apiClient.CallTrAsync(
            "g3203",
            new JsonObject
            {
                ["delaygb"] = "R",
                ["keysymbol"] = symbol.KeySymbol,
                ["exchcd"] = symbol.Exchcd,
                ["symbol"] = symbol.Symbol,
                ["ncnt"] = unit,
                ["qrycnt"] = count,
                // comp_yn = 압축여부 (Y=압축, N=비압축). Forced to "N":
                // asking LS for the compressed path corrupts the price
                // fields on overseas charts (rsp_cd=IGW40014, e.g.
                // "id=[시가(open)] in.data=[ 190.8400(<] dPoint=[8]"
                // — control bytes prefixing what should be a USD price).
                // KR chart TRs use "N" for the same reason.
                ["comp_yn"] = "N",
                ["sdate"] = from ?? "",
                ["edate"] = to ?? "",
                ["cts_date"] = "",
                ["cts_time"] = "",
            },
            cancellationToken: ct).ConfigureAwait(false);
        EnsureSuccess(response);
        return ParseOverseasCandles(response.GetBlock("g3203OutBlock1"), periodType: "min", max: count);
    }

    static async Task<List<Candle>> FetchOverseasTickAsync(
        LsApiClient apiClient,
        OverseasSymbol symbol,
        int unit,
        int count,
        string? from,
        string? to,
        CancellationToken ct)
    {
        LsTrResponse response = await apiClient.CallTrAsync(
            "g3202",
            new JsonObject
            {
                ["delaygb"] = "R",
                ["keysymbol"] = symbol.KeySymbol,
                ["exchcd"] = symbol.Exchcd,
                ["symbol"] = symbol.Symbol,
                ["ncnt"] = unit,
                ["qrycnt"] = count,
                // comp_yn = 압축여부 (Y=압축, N=비압축). Forced to "N":
                // asking LS for the compressed path corrupts the price
                // fields on overseas charts (rsp_cd=IGW40014, e.g.
                // "id=[시가(open)] in.data=[ 190.8400(<] dPoint=[8]"
                // — control bytes prefixing what should be a USD price).
                // KR chart TRs use "N" for the same reason.
                ["comp_yn"] = "N",
                ["sdate"] = from ?? "",
                ["edate"] = to ?? "",
                ["cts_seq"] = 0,
            },
            cancellationToken: ct).ConfigureAwait(false);
        EnsureSuccess(response);
        return ParseOverseasCandles(response.GetBlock("g3202OutBlock1"), periodType: "tick", max: count);
    }

    static List<Candle> ParseOverseasCandles(JsonElement? array, string periodType, int max)
    {
        var candles = new List<Candle>();
        if (array is null || array.Value.ValueKind != JsonValueKind.Array)
            return candles;

        foreach (JsonElement row in array.Value.EnumerateArray())
        {
            string? date = row.ReadString("date");
            if (string.IsNullOrWhiteSpace(date))
                continue;

            DateTime parsed;
            if (periodType is "min" or "tick")
            {
                string time = (row.ReadString("loctime") ?? "000000").PadLeft(6, '0');
                if (!DateTime.TryParseExact(date.PadLeft(8, '0') + time, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
                    continue;
            }
            else if (!DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
            {
                continue;
            }

            candles.Add(new Candle(
                Date: parsed,
                Open: row.ReadDecimal("open"),
                High: row.ReadDecimal("high"),
                Low: row.ReadDecimal("low"),
                Close: row.ReadDecimal("close"),
                Volume: row.ReadLong(periodType is "day" or "week" or "month" or "year" ? "volume" : "exevol"),
                Value: row.ReadLong("amount")));

            if (candles.Count >= max)
                break;
        }

        candles.Sort((a, b) => a.Date.CompareTo(b.Date));
        return candles;
    }

    static IEnumerable<object> SerializeCandles(IReadOnlyList<Candle> candles, string periodType) =>
        candles.Select(c => new
        {
            date = c.Date.ToString(periodType is "min" or "tick" ? "yyyy-MM-ddTHH:mm:ss" : "yyyy-MM-dd", CultureInfo.InvariantCulture),
            open = c.Open,
            high = c.High,
            low = c.Low,
            close = c.Close,
            volume = c.Volume,
            value = c.Value,
        });

    static JsonObject BuildSymbolInBlock(string blockName, OverseasSymbol symbol) => new()
    {
        ["delaygb"] = "R",
        ["keysymbol"] = symbol.KeySymbol,
        ["exchcd"] = symbol.Exchcd,
        ["symbol"] = symbol.Symbol,
    };

    static object BuildProfile(JsonElement p) => new
    {
        english_name = p.ReadString("engname"),
        exchange_name = p.ReadString("exchange_name"),
        nation_name = p.ReadString("nation_name"),
        security_type = p.ReadString("instname"),
        listed_shares = p.ReadLong("share"),
        market_cap = p.ReadLong("shareprc"),
        order_tick = p.ReadDecimal("untprc"),
        bid_lot_size = p.ReadString("bidlotsize2") ?? p.ReadString("bidlotsize"),
        ask_lot_size = p.ReadString("asklotsize2") ?? p.ReadString("asklotsize"),
        previous_close = p.ReadDecimal("pcls"),
        base_price = p.ReadDecimal("clos"),
        exchange_rate = p.ReadDouble("exrate"),
    };

    static object BuildOrderBook(JsonElement b)
    {
        // LS may omit higher levels when the book is shallow; preserve "missing"
        // semantics as nulls so the model doesn't read 0 as a real quote.
        var levels = Enumerable.Range(1, 10).Select(i => new
        {
            level = i,
            ask = ReadOptionalDecimal(b, $"offerho{i}"),
            ask_count = b.ReadString($"offercnt{i}"),
            ask_size = ReadOptionalLong(b, $"offerrem{i}"),
            bid = ReadOptionalDecimal(b, $"bidho{i}"),
            bid_count = b.ReadString($"bidcnt{i}"),
            bid_size = ReadOptionalLong(b, $"bidrem{i}"),
        }).Where(l => l.ask is not null || l.bid is not null).ToList();

        return new
        {
            quote_time = b.ReadString("hotime"),
            total_ask_count = b.ReadString("offercnt"),
            total_bid_count = b.ReadString("bidcnt"),
            total_ask_size = b.ReadLong("offer"),
            total_bid_size = b.ReadLong("bid"),
            levels,
        };
    }

    static bool TryResolveOverseasSymbol(
        string? symbol,
        string? exchcd,
        string? keysymbol,
        out OverseasSymbol resolved,
        out string? error)
    {
        string rawKey = keysymbol?.Trim().ToUpperInvariant() ?? "";
        string rawSymbol = symbol?.Trim().ToUpperInvariant() ?? "";

        // Auto-promote a stray prefix like "82TSLA" passed as `symbol` into a
        // keysymbol. US tickers are exclusively alphabetic (and at most 5
        // chars), so a "81"/"82" prefix followed by a letter is unambiguously
        // an exchange code, not a real ticker — collisions with real tickers
        // do not occur in practice.
        if (string.IsNullOrWhiteSpace(rawKey) && rawSymbol.Length >= 3 && IsExchangeCode(rawSymbol[..2]) && !char.IsDigit(rawSymbol[2]))
        {
            rawKey = rawSymbol;
            rawSymbol = rawSymbol[2..];
        }

        string exchange = NormalizeExchangeCode(exchcd);
        if (!string.IsNullOrWhiteSpace(rawKey))
        {
            if (rawKey.Length < 3 || !IsExchangeCode(rawKey[..2]))
            {
                resolved = default;
                error = "keysymbol must look like '82TSLA' or '81IBM'.";
                return false;
            }

            exchange = rawKey[..2];
            if (string.IsNullOrWhiteSpace(rawSymbol))
                rawSymbol = rawKey[2..];
        }

        if (string.IsNullOrWhiteSpace(rawSymbol))
        {
            resolved = default;
            error = "symbol is required unless keysymbol is supplied.";
            return false;
        }

        resolved = new OverseasSymbol(rawSymbol, exchange, exchange + rawSymbol);
        error = null;
        return true;
    }

    static string NormalizeExchangeCode(string? raw)
    {
        string key = raw?.Trim().ToLowerInvariant() ?? "";
        return key switch
        {
            "" or "82" or "2" or "nasdaq" or "nas" => "82",
            "81" or "1" or "nyse" or "amex" or "newyork" or "new_york" => "81",
            _ => raw!.Trim(),
        };
    }

    static string NormalizeExchangeGroup(string? raw)
    {
        string key = raw?.Trim().ToLowerInvariant() ?? "";
        return key switch
        {
            "" or "all" or "0" => "0",
            "82" or "2" or "nasdaq" or "nas" => "2",
            "81" or "1" or "nyse" or "amex" or "newyork" or "new_york" => "1",
            _ => key,
        };
    }

    static string ExchangeName(string? exchcd) => exchcd switch
    {
        "81" => "nyse_amex",
        "82" => "nasdaq",
        _ => exchcd ?? "unknown",
    };

    static string? CurrencyHint(string? exchcd) => exchcd switch
    {
        "81" or "82" => "USD",
        _ => null,
    };

    /// <summary>
    /// IANA timezone the candle <c>date</c>/<c>loctime</c> fields are expressed
    /// in for a given LS exchange code. Used to disambiguate "this is a NYSE
    /// trading day, not a Seoul calendar day" on the chart response so the
    /// model can reason about Asia-vs-US date offsets without guessing.
    /// </summary>
    /// <remarks>
    /// Daily/weekly/monthly bars (g3204) are indexed by the local trading day;
    /// minute/tick bars (g3203/g3202) carry an explicit <c>loctime</c> in the
    /// same zone. g3102's <c>kordate</c>/<c>kortime</c> pair is the only place
    /// LS ships a Korea-local equivalent — we surface the local-market hint
    /// since that's what the chart axis actually shows.
    /// </remarks>
    static string? BarTimezone(string? exchcd) => exchcd switch
    {
        "81" or "82" => "America/New_York",
        _ => null,
    };

    static bool IsExchangeCode(string value) => value is "81" or "82";

    static bool ContainsAny(string? a, string? b, string? c, string? d, string needle) =>
        Contains(a, needle) || Contains(b, needle) || Contains(c, needle) || Contains(d, needle);

    static bool Contains(string? value, string needle) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(needle, StringComparison.OrdinalIgnoreCase);

    static double Signed(double value, string? sign) => IndustryDataCache.ApplySign(value, sign);

    static string BusinessError(LsTrResponse response, string trCode, OverseasSymbol symbol) =>
        McpJson.Error("LS reported a business-level error.", new
        {
            rsp_cd = response.RspCode,
            rsp_msg = response.RspMessage,
            tr_cd = trCode,
            symbol = symbol.Symbol,
            exchcd = symbol.Exchcd,
            keysymbol = symbol.KeySymbol,
        });

    static void EnsureSuccess(LsTrResponse response)
    {
        if (!response.IsSuccess)
            throw new LsTrException(
                response.TrCode,
                $"LS reported business error rsp_cd={response.RspCode} ({response.RspMessage}).",
                statusCode: response.StatusCode,
                responseBody: response.RawBody);
    }

    static int CalendarDaysBack(string gubun, int count) => gubun switch
    {
        "2" => Math.Max(count * 3 + 7, 14),
        "3" => Math.Max(count * 8, 30),
        "4" => Math.Max(count * 32, 60),
        "5" => Math.Max(count * 367, 365 * 2),
        _ => Math.Max(count * 3 + 7, 14),
    };

    static string ResolveOutputMode(string? outputMode, bool includeChart)
    {
        if (!string.IsNullOrWhiteSpace(outputMode))
            return outputMode.Trim().ToLowerInvariant();
        return includeChart ? "display" : "analyze";
    }

    // Warm-up sizing — kept identical to GetChartTool so KR/US charts produce
    // comparable AnalyticalSummary coverage at matching periods.

    static int SummaryWarmup(string period) => period switch
    {
        "day" => 240,
        "week" => 120,
        "month" => 120,
        "year" => 10,
        "min" => 200,
        "tick" => 0,
        _ => 120,
    };

    static int RequiredWarmup(IReadOnlyList<IndicatorSpec> specs)
    {
        int warmup = 0;
        foreach (IndicatorSpec spec in specs)
        {
            int need = spec.Kind switch
            {
                "ma" or "bb" => (int)spec.Args[0],
                "ema" or "rsi" => (int)spec.Args[0] * 3,
                "macd" => ((int)spec.Args[1] + (int)spec.Args[2]) * 3,
                _ => 0,
            };
            warmup = Math.Max(warmup, need);
        }
        return warmup;
    }

    static List<IndicatorSpec> MergeIndicatorSpecs(IReadOnlyList<IndicatorSpec> existing, IndicatorSpec added)
    {
        var merged = new List<IndicatorSpec>(existing);
        if (!merged.Any(s => string.Equals(s.Raw, added.Raw, StringComparison.OrdinalIgnoreCase)))
            merged.Add(added);
        return merged;
    }

    static object SummarizeIndicator(
        string requestedSpec,
        IReadOnlyDictionary<string, IReadOnlyList<double?>> indicators)
    {
        var latest = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach ((string key, IReadOnlyList<double?> values) in indicators)
        {
            if (string.Equals(key, requestedSpec, StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith(requestedSpec + ".", StringComparison.OrdinalIgnoreCase))
            {
                latest[key] = values.LastOrDefault(v => v.HasValue);
            }
        }
        return new { spec = requestedSpec, latest };
    }

    static decimal? ReadOptionalDecimal(JsonElement element, string property) =>
        element.TryGetProperty(property, out _) ? element.ReadDecimal(property) : null;

    static long? ReadOptionalLong(JsonElement element, string property) =>
        element.TryGetProperty(property, out _) ? element.ReadLong(property) : null;

    // ============================================================
    // Internal types — assembly-visible so GetChartTool can route here.
    // ============================================================

    internal readonly record struct OverseasSymbol(string Symbol, string Exchcd, string KeySymbol);

    /// <summary>
    /// One row returned by <see cref="SearchOverseasStock"/>. Sourced from
    /// either the g3190 master scan or a g3104 ticker probe — fields that
    /// only g3190 supplies (ISIN, listed_date, fractional_trading) are null
    /// when the row came from the probe.
    /// </summary>
    internal sealed record DiscoveredSymbol(
        string? KeySymbol,
        string? Symbol,
        string? Exchcd,
        string? Exchange,
        string? Natcode,
        string? KoreanName,
        string? EnglishName,
        string? Currency,
        string? Isin,
        string? ListedDate,
        string? Suspend,
        string? Sellonly,
        string? FractionalTrading);

    internal sealed record OverseasFrame(
        string PeriodType,
        string TrCode,
        IReadOnlyList<Candle> Candles,
        IReadOnlyDictionary<string, IReadOnlyList<double?>> Indicators,
        ChartContext Context,
        AnalyticalSummary Summary);

    internal sealed record OverseasChartDataset(
        OverseasSymbol Symbol,
        string? Name,
        string PeriodType,
        string TrCode,
        int Unit,
        string? SourceFrom,
        string? SourceTo,
        IReadOnlyList<Candle> Candles,
        IReadOnlyDictionary<string, IReadOnlyList<double?>> Indicators,
        IReadOnlyList<IndicatorSpec> IndicatorSpecs,
        ChartContext Context,
        AnalyticalSummary Summary,
        bool Adjusted,
        DateTimeOffset CreatedAtUtc);
}
