using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that returns market-wide top-stock rankings: gainers/losers,
/// market cap, volume, trading value, and volume surges.
/// </summary>
[McpServerToolType]
public static class GetTopStocksTool
{
    /// <summary>Maximum normalized rows returned by one tool call.</summary>
    public const int MaxRows = 100;

    const int MaxPages = 5;

    // LS ranking TRs (t1441/t1452/t1463/t1466) declare sprice/eprice as 8-digit
    // Number fields. eprice=0 is treated as "no price filter" — and it also
    // suppresses sprice — so a min_price floor with no max_price cap needs a
    // non-zero eprice. 99,999,999 is the largest value the 8-digit field holds.
    const long PriceFilterCeiling = 99_999_999;

    /// <summary>
    /// Returns one of LS's top-stock ranking screens as normalized JSON.
    /// </summary>
    /// <param name="apiClient">Injected LS API client.</param>
    /// <param name="kind">Ranking kind: gainers, losers, unchanged, market_cap, volume, amount, volume_surge.</param>
    /// <param name="market">Market filter: all, kospi, kosdaq.</param>
    /// <param name="limit">Maximum rows to return, default 20, max 100.</param>
    /// <param name="basis">today or previous_day where the underlying TR supports it.</param>
    /// <param name="exchange">KRX, NXT, or unified. Applies to TRs that support exchange selection.</param>
    /// <param name="min_price">Optional minimum current price.</param>
    /// <param name="max_price">Optional maximum current price.</param>
    /// <param name="min_volume">Optional minimum volume.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON with normalized ranking rows and the underlying TR metadata.</returns>
    [McpServerTool(Name = "ls_get_top_stocks")]
    [Description("""
        Returns Korean stock ranking screens: top gainers/losers/unchanged, market-cap leaders, volume leaders, trading-value leaders, and volume-surge leaders. Wraps LS TRs t1441, t1444, t1452, t1463, and t1466 behind one kind parameter.

        USE WHEN: the user asks for market-wide screeners such as "오늘 급등주", "하락률 상위", "시총 상위", "거래량 상위", "거래대금 상위", "거래 급증 종목", or a quick idea-generation list before drilling into ls_get_quote / ls_get_chart / ls_get_stock_info.
        AVOID WHEN: the user already named a specific stock and wants detail — use ls_get_quote, ls_get_chart, or ls_get_stock_info instead.

        kind: gainers | losers | unchanged | market_cap | volume | amount | volume_surge.
        market: all | kospi | kosdaq. For kind=market_cap and market=all, the tool calls KOSPI and KOSDAQ ranking screens and merges by market cap.
        basis: today | previous_day. Ignored for market_cap and volume_surge.
        exchange: unified | krx | nxt. Applies to gainers/losers/unchanged, amount, and volume_surge.
        Price and volume filters apply only where the underlying LS TR supports them; kind=market_cap ignores those filters.
        """)]
    public static async Task<string> GetTopStocks(
        LsApiClient apiClient,
        [Description("Ranking kind: gainers, losers, unchanged, market_cap, volume, amount, or volume_surge.")]
        string kind,
        [Description("Market filter: all, kospi, or kosdaq. Default all.")]
        string market = "all",
        [Description("Maximum rows to return (1-100). Default 20.")]
        int limit = 20,
        [Description("today or previous_day. Applies to gainers/losers/unchanged, volume, and amount. Default today.")]
        string basis = "today",
        [Description("unified, krx, or nxt. Applies where LS supports exchange selection. Default unified.")]
        string exchange = "unified",
        [Description("Optional minimum current price.")]
        long min_price = 0,
        [Description("Optional maximum current price. 0 means no upper bound.")]
        long max_price = 0,
        [Description("Optional minimum volume.")]
        long min_volume = 0,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeKind(kind, out string normalizedKind, out string? kindError))
            return McpJson.Error(kindError!);
        if (!TryNormalizeMarket(market, out string normalizedMarket, out string? marketError))
            return McpJson.Error(marketError!);
        if (!TryNormalizeBasis(basis, out string normalizedBasis, out string? basisError))
            return McpJson.Error(basisError!);
        if (!TryNormalizeExchange(exchange, out string exchangeCode, out string normalizedExchange, out string? exchangeError))
            return McpJson.Error(exchangeError!);
        if (limit < 1 || limit > MaxRows)
            return McpJson.Error($"limit must be between 1 and {MaxRows}.");
        if (min_price < 0 || max_price < 0 || min_volume < 0)
            return McpJson.Error("min_price, max_price, and min_volume must be zero or positive.");
        if (max_price > 0 && max_price < min_price)
            return McpJson.Error("max_price must be greater than or equal to min_price.");

        try
        {
            RankingSpec spec = BuildSpec(
                normalizedKind, normalizedMarket, normalizedBasis, exchangeCode, min_price, max_price, min_volume);

            List<RankRow> rows = normalizedKind == "market_cap" && normalizedMarket == "all"
                ? await FetchMergedMarketCapAsync(apiClient, limit, cancellationToken)
                : await FetchRowsAsync(apiClient, spec, limit, cancellationToken);

            rows = rows
                .Take(limit)
                .Select((row, index) => row with { Rank = index + 1 })
                .ToList();

            var payload = new
            {
                kind = normalizedKind,
                market = normalizedMarket,
                basis = normalizedKind is "market_cap" or "volume_surge" ? null : normalizedBasis,
                exchange = normalizedKind is "market_cap" or "volume" ? null : normalizedExchange,
                tr_codes = spec.TrCodes.Distinct(StringComparer.Ordinal).ToArray(),
                count = rows.Count,
                rows,
            };
            return JsonSerializer.Serialize(payload, McpJson.Tool);
        }
        catch (LsAuthException ex)
        {
            return McpJson.Error("Authentication failed.", new { reason = ex.Message });
        }
        catch (LsBusinessErrorException ex)
        {
            return McpJson.Error("LS reported a business-level error.", new
            {
                rsp_cd = ex.RspCode,
                rsp_msg = ex.RspMessage,
            });
        }
        catch (LsTrException ex)
        {
            return McpJson.Error("TR call failed.", new { reason = ex.Message, status = ex.StatusCode });
        }
    }

    static async Task<List<RankRow>> FetchMergedMarketCapAsync(
        LsApiClient apiClient,
        int topN,
        CancellationToken ct)
    {
        RankingSpec kospi = BuildSpec("market_cap", "kospi", "today", "U", 0, 0, 0);
        RankingSpec kosdaq = BuildSpec("market_cap", "kosdaq", "today", "U", 0, 0, 0);

        List<RankRow> rows = new();
        rows.AddRange(await FetchRowsAsync(apiClient, kospi, topN, ct));
        rows.AddRange(await FetchRowsAsync(apiClient, kosdaq, topN, ct));

        return rows
            .OrderByDescending(r => r.MarketCap)
            .Take(topN)
            .ToList();
    }

    static async Task<List<RankRow>> FetchRowsAsync(
        LsApiClient apiClient,
        RankingSpec spec,
        int topN,
        CancellationToken ct)
    {
        var rows = new List<RankRow>(topN);
        int idx = 0;

        for (int page = 0; page < MaxPages && rows.Count < topN; page++)
        {
            JsonObject inBlock = spec.BuildInBlock(idx);
            LsTrResponse response = await apiClient.CallTrAsync(spec.TrCode, inBlock, cancellationToken: ct);

            if (!response.IsSuccess)
                throw new LsBusinessErrorException(response.RspCode, response.RspMessage);

            JsonElement? array = response.GetBlock($"{spec.TrCode}OutBlock1");
            if (array is null || array.Value.ValueKind != JsonValueKind.Array)
                break;

            string? snapshotTime = response
                .GetBlock($"{spec.TrCode}OutBlock")
                ?.ReadString("hhmm");

            foreach (JsonElement row in array.Value.EnumerateArray())
            {
                rows.Add(ParseRow(spec, row, snapshotTime));
                if (rows.Count >= topN)
                    break;
            }

            idx = response.GetBlock($"{spec.TrCode}OutBlock")?.ReadLong("idx") is long next ? (int)next : 0;
            if (idx <= 0)
                break;
        }

        return rows;
    }

    static RankRow ParseRow(RankingSpec spec, JsonElement row, string? snapshotTime) =>
        spec.Kind switch
        {
            "market_cap" => Common(row, spec, snapshotTime) with
            {
                VolumeSharePercent = row.ReadDouble("vol_rate"),
                MarketCap = row.ReadLong("total"),
                MarketCapWeightPercent = row.ReadDouble("rate"),
                ForeignOwnershipPercent = row.ReadDouble("for_rate"),
            },
            "volume" => Common(row, spec, snapshotTime) with
            {
                TurnoverPercent = row.ReadDouble("vol"),
                PreviousVolume = row.ReadLong("jnilvolume"),
                VolumeChangePercent = row.ReadDouble("bef_diff"),
            },
            "amount" => Common(row, spec, snapshotTime) with
            {
                Value = row.ReadLong("value"),
                PreviousValue = row.ReadLong("jnilvalue"),
                ValueChangePercent = row.ReadDouble("bef_diff"),
                PreviousVolume = row.ReadLong("jnilvolume"),
                MarketCap = row.ReadLong("total"),
                ExchangeShcode = row.ReadString("ex_shcode"),
            },
            "volume_surge" => Common(row, spec, snapshotTime) with
            {
                ReferenceVolume = row.ReadLong("stdvolume"),
                VolumeSurgePercent = row.ReadDouble("voldiff"),
                Open = row.ReadLong("open"),
                High = row.ReadLong("high"),
                Low = row.ReadLong("low"),
                ExchangeShcode = row.ReadString("ex_shcode"),
            },
            _ => Common(row, spec, snapshotTime) with
            {
                BestAsk = row.ReadLong("offerho1"),
                BestAskSize = row.ReadLong("offerrem1"),
                BestBid = row.ReadLong("bidho1"),
                BestBidSize = row.ReadLong("bidrem1"),
                ConsecutiveDays = row.ReadLong("updaycnt"),
                PreviousChangePercent = row.ReadDouble("jnildiff"),
                VolumeChangePercent = row.ReadDouble("voldiff"),
                Value = row.ReadLong("value"),
                MarketCap = row.ReadLong("total"),
                Open = row.ReadLong("open"),
                High = row.ReadLong("high"),
                Low = row.ReadLong("low"),
                ExchangeShcode = row.ReadString("ex_shcode"),
            },
        };

    static RankRow Common(JsonElement row, RankingSpec spec, string? snapshotTime) =>
        new(
            Rank: 0,
            TrCode: spec.TrCode,
            Shcode: row.ReadString("shcode"),
            Name: row.ReadString("hname"),
            Price: row.ReadLong("price"),
            Sign: row.ReadString("sign"),
            Change: row.ReadLong("change"),
            ChangePercent: row.ReadDouble("diff"),
            Volume: row.ReadLong("volume"))
        {
            SnapshotTime = snapshotTime,
        };

    static RankingSpec BuildSpec(
        string kind,
        string market,
        string basis,
        string exchangeCode,
        long minPrice,
        long maxPrice,
        long minVolume)
    {
        // When a min_price floor is set without a max_price cap, send the
        // 8-digit ceiling as eprice; LS ignores the whole price filter (sprice
        // included) when eprice is 0.
        long eprice = maxPrice > 0 ? maxPrice
            : minPrice > 0 ? PriceFilterCeiling
            : 0;

        return kind switch
        {
            "gainers" or "losers" or "unchanged" => new RankingSpec(
                kind,
                "t1441",
                new[] { "t1441" },
                idx => new JsonObject
                {
                    ["gubun1"] = MarketGubun(market),
                    ["gubun2"] = kind == "gainers" ? "0" : kind == "losers" ? "1" : "2",
                    ["gubun3"] = basis == "today" ? "0" : "1",
                    ["jc_num"] = 0,
                    ["sprice"] = minPrice,
                    ["eprice"] = eprice,
                    ["volume"] = minVolume,
                    ["idx"] = idx,
                    ["jc_num2"] = 0,
                    ["exchgubun"] = exchangeCode,
                }),
            "market_cap" => new RankingSpec(
                kind,
                "t1444",
                market == "all" ? new[] { "t1444", "t1444" } : new[] { "t1444" },
                idx => new JsonObject
                {
                    ["upcode"] = market == "kosdaq" ? "301" : "001",
                    ["idx"] = idx,
                }),
            "volume" => new RankingSpec(
                kind,
                "t1452",
                new[] { "t1452" },
                idx => new JsonObject
                {
                    ["gubun"] = MarketGubun(market),
                    ["jnilgubun"] = basis == "today" ? "1" : "2",
                    ["sdiff"] = 0,
                    ["ediff"] = 0,
                    ["jc_num"] = 0,
                    ["sprice"] = minPrice,
                    ["eprice"] = eprice,
                    ["volume"] = minVolume,
                    ["idx"] = idx,
                }),
            "amount" => new RankingSpec(
                kind,
                "t1463",
                new[] { "t1463" },
                idx => new JsonObject
                {
                    ["gubun"] = MarketGubun(market),
                    ["jnilgubun"] = basis == "today" ? "0" : "1",
                    ["jc_num"] = 0,
                    ["sprice"] = minPrice,
                    ["eprice"] = eprice,
                    ["volume"] = minVolume,
                    ["idx"] = idx,
                    ["jc_num2"] = 0,
                    ["exchgubun"] = exchangeCode,
                }),
            "volume_surge" => new RankingSpec(
                kind,
                "t1466",
                new[] { "t1466" },
                idx => new JsonObject
                {
                    ["gubun"] = MarketGubun(market),
                    ["type1"] = "0",
                    ["type2"] = "0",
                    ["jc_num"] = 0,
                    ["sprice"] = minPrice,
                    ["eprice"] = eprice,
                    ["volume"] = minVolume,
                    ["idx"] = idx,
                    ["jc_num2"] = 0,
                    ["exchgubun"] = exchangeCode,
                }),
            _ => throw new InvalidOperationException($"Unknown kind '{kind}'."),
        };
    }

    static string MarketGubun(string market) => market switch
    {
        "kospi" => "1",
        "kosdaq" => "2",
        _ => "0",
    };

    static bool TryNormalizeKind(string? raw, out string kind, out string? error)
    {
        kind = raw?.Trim().ToLowerInvariant().Replace("-", "_") ?? "";
        kind = kind switch
        {
            "gainer" or "gain" or "riser" or "up" => "gainers",
            "loser" or "fall" or "down" => "losers",
            "flat" => "unchanged",
            "marketcap" or "market_capitalization" or "cap" => "market_cap",
            "trading_value" or "value" or "turnover_amount" => "amount",
            "volume_spike" or "surge" => "volume_surge",
            _ => kind,
        };

        if (kind is "gainers" or "losers" or "unchanged" or "market_cap" or "volume" or "amount" or "volume_surge")
        {
            error = null;
            return true;
        }

        error = "kind must be one of: gainers, losers, unchanged, market_cap, volume, amount, volume_surge.";
        return false;
    }

    static bool TryNormalizeMarket(string? raw, out string market, out string? error)
    {
        market = raw?.Trim().ToLowerInvariant().Replace("-", "_") ?? "all";
        market = market switch
        {
            "" or "all" or "전체" => "all",
            "kospi" or "kp" or "1" or "코스피" => "kospi",
            "kosdaq" or "kd" or "2" or "코스닥" => "kosdaq",
            _ => market,
        };

        if (market is "all" or "kospi" or "kosdaq")
        {
            error = null;
            return true;
        }

        error = "market must be one of: all, kospi, kosdaq.";
        return false;
    }

    static bool TryNormalizeBasis(string? raw, out string basis, out string? error)
    {
        basis = raw?.Trim().ToLowerInvariant().Replace("-", "_") ?? "today";
        basis = basis switch
        {
            "" or "today" or "당일" => "today",
            "previous" or "previous_day" or "yesterday" or "전일" => "previous_day",
            _ => basis,
        };

        if (basis is "today" or "previous_day")
        {
            error = null;
            return true;
        }

        error = "basis must be either today or previous_day.";
        return false;
    }

    static bool TryNormalizeExchange(string? raw, out string code, out string exchange, out string? error)
    {
        exchange = raw?.Trim().ToLowerInvariant().Replace("-", "_") ?? "unified";
        (code, exchange) = exchange switch
        {
            "" or "unified" or "u" or "통합" => ("U", "unified"),
            "krx" or "k" => ("K", "krx"),
            "nxt" or "n" => ("N", "nxt"),
            _ => ("", exchange),
        };

        if (code.Length > 0)
        {
            error = null;
            return true;
        }

        error = "exchange must be one of: unified, krx, nxt.";
        return false;
    }

    sealed record RankingSpec(
        string Kind,
        string TrCode,
        IReadOnlyList<string> TrCodes,
        Func<int, JsonObject> BuildInBlock);

    sealed class LsBusinessErrorException(string? rspCode, string? rspMessage) : Exception
    {
        public string? RspCode { get; } = rspCode;
        public string? RspMessage { get; } = rspMessage;
    }

    sealed record RankRow(
        int Rank,
        string TrCode,
        string? Shcode,
        string? Name,
        long Price,
        string? Sign,
        long Change,
        double ChangePercent,
        long Volume)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SnapshotTime { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Value { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? MarketCap { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Open { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? High { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Low { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ExchangeShcode { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? BestAsk { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? BestAskSize { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? BestBid { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? BestBidSize { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? ConsecutiveDays { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? PreviousChangePercent { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? VolumeChangePercent { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? VolumeSharePercent { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? MarketCapWeightPercent { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? ForeignOwnershipPercent { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? TurnoverPercent { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? PreviousVolume { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? PreviousValue { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? ValueChangePercent { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? ReferenceVolume { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? VolumeSurgePercent { get; init; }
    }
}
