using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;
using RedoxNet.LsOpenApi.Core.Time;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tools for the LS xingTrader Q-Click (씽큐스마트) signal catalog.
/// </summary>
/// <remarks>
/// <para>Three tools, one shared cache:</para>
/// <list type="bullet">
///   <item><description><c>ls_list_screeners</c> — enumerate the LS-curated catalog.</description></item>
///   <item><description><c>ls_run_screener</c> — single-signal execution. Accepts exact name/id or a Korean keyword; ambiguous keywords return a candidate list plus the matching group's full mini-catalog (β policy from SPEC-v1.4 §3).</description></item>
///   <item><description><c>ls_combine_screeners</c> — multi-signal AND/OR — the slice-B signature, running 2-8 signals and combining matched stocks by shcode set operation. No HTS screen can express this directly.</description></item>
/// </list>
/// <para>Catalog is cached for the process lifetime: it is LS-curated, account-independent, and stable enough that one fetch per server start is sufficient. Tests reset it via <see cref="ResetCatalogForTesting"/>.</para>
/// </remarks>
[McpServerToolType]
public static class ScreenerTools
{
    const int MaxRows = 100;
    const int MaxSignalsPerCombination = 8;

    // LS spec doc enumerates search_gb 0..3, but HTS [1801] also shows a
    // "급변종목" 5th group (가격급등/급락, 거래량 급증 분봉 시그널).
    // Probing search_gb=4 to find out whether LS exposes that group via
    // t1826 or rejects it (in which case those signals are surfaced via
    // separate TRs like t1442 and we already expose them through
    // ls_get_top_stocks(kind="volume_surge")).
    static readonly ScreenerGroup[] Groups =
    [
        new("0", "core", "핵심검색"),
        new("1", "indicator", "지표검색"),
        new("2", "market_trend", "시세동향"),
        new("3", "investor_trend", "투자자동향"),
        new("4", "rapid_change", "급변종목"),
    ];

    static readonly SemaphoreSlim _catalogLock = new(1, 1);
    static IReadOnlyList<ScreenerInfo>? _catalog;

    /// <summary>
    /// Drops the process-lifetime catalog cache so the next call re-fetches
    /// from t1826. Test-only; production code should let the cache persist
    /// for the server lifetime.
    /// </summary>
    internal static void ResetCatalogForTesting()
    {
        _catalogLock.Wait();
        try { _catalog = null; }
        finally { _catalogLock.Release(); }
    }

    [McpServerTool(Name = "ls_list_screeners")]
    [Description("""
        Lists LS's curated Q-Click / 씽큐스마트 stock-signal catalog via t1826. These are standard signals LS maintains (e.g. 이평 골든크로스, 20일 매물대 상향돌파, 외인 3일연속 순매수); every account sees the same 99-signal catalog from the first call. NOT user-authored conditions: HTS [1892] (KRX)조건검색 is a separate system that does not flow into this surface.

        USE WHEN: the user asks "Q-클릭 조건 뭐 있어?", "LS가 제공하는 시그널 목록", "골든크로스 같은 시그널 있어?", or the model needs to learn the exact catalog names before running / combining signals.
        AVOID WHEN: the user already named a signal (use ls_run_screener) or asked for a compound condition (use ls_combine_screeners — both tools accept Korean keywords directly and surface candidates if a keyword is ambiguous).

        search_group: all (default) returns the full catalog, or filter by core (핵심검색, 23) / indicator (지표검색, 33) / market_trend (시세동향, 16) / investor_trend (투자자동향, 15) / rapid_change (급변종목, 12). LS codes 0/1/2/3/4 also accepted; LS spec doc enumerates only 0-3 but search_gb=4 (rapid_change, ids 6401-6412) is verified active as of 2026-05-25.
        """)]
    public static async Task<string> ListScreeners(
        LsApiClient apiClient,
        [Description("Signal group: all (default, 99 signals) / core (23) / indicator (33) / market_trend (16) / investor_trend (15) / rapid_change (12). LS codes 0/1/2/3/4 also accepted.")]
        string search_group = "all",
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveGroups(search_group, out IReadOnlyList<ScreenerGroup> selectedGroups, out string? groupError))
            return McpJson.Error(groupError!);

        try
        {
            // Always go through the cache so the first list call also seeds
            // the cache used by ls_run_screener / ls_combine_screeners.
            IReadOnlyList<ScreenerInfo> catalog = await GetCatalogAsync(apiClient, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ScreenerInfo> filtered = selectedGroups.Count == Groups.Length
                ? catalog
                : catalog.Where(s => selectedGroups.Any(g => g.Name == s.Group)).ToList();

            var payload = new
            {
                search_group = NormalizeGroup(search_group),
                count = filtered.Count,
                results = filtered,
                source_tr = "t1826",
                note = filtered.Count == 0
                    ? "LS returned an empty Q-Click signal catalog for the requested group. The catalog is account-independent and should always be present; an empty response usually signals an OpenAPI permission or service issue rather than missing user data."
                    : null,
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
        catch (LsBusinessErrorException ex)
        {
            return McpJson.Error("LS reported a business-level error.", new
            {
                rsp_cd = ex.RspCode,
                rsp_msg = ex.RspMessage,
                source_tr = ex.SourceTr,
            });
        }
    }

    [McpServerTool(Name = "ls_run_screener")]
    [Description("""
        Runs one of LS's curated Q-Click / 씽큐스마트 stock signals via t1825 and returns matching Korean stocks. Signals are LS-maintained (catalog of 99 across five groups — 핵심검색 / 지표검색 / 시세동향 / 투자자동향 / 급변종목, ids 6001-6412) and identical for every account.

        USE WHEN: the user names a signal exactly ("이평 골든크로스(5,20) 매칭"), gives a 4-digit search_cd ("6116 실행"), or describes a setup that maps to a single catalog entry ("골든크로스 뜬 종목" → if ambiguous between (5,20) and (20,60), the tool returns both candidates + the indicator group catalog so the model can disambiguate).
        AVOID WHEN: the user wants MULTIPLE signals combined ("골든크로스 + 외인 순매수") — use ls_combine_screeners; user-authored HTS [1892] conditions (not exposed in v1.4); arbitrary expression-based screening (use ls_get_top_stocks / ls_get_high_low_stocks / ls_get_fundamentals_rank).

        name_or_id accepts: (1) exact 4-character search_cd, (2) exact catalog name (case-insensitive), or (3) a Korean keyword like "골든크로스" — the tool matches against the cached catalog. An ambiguous keyword returns an error envelope with the candidates and the full group catalog (β policy) so the model can pick the precise entry without an extra ls_list_screeners round trip.
        market: all (default), kospi, or kosdaq. Results carry shcode so follow-up calls can use ls_get_quote / ls_get_chart / ls_get_stock_info. data_as_of + query_date_resolution match the cross-cutting envelope used by other daily-snapshot tools.
        """)]
    public static async Task<string> RunScreener(
        LsApiClient apiClient,
        [Description("Q-Click signal: exact name (\"이평 골든크로스(5,20)\", case-insensitive), 4-character search_cd (\"6116\"), or Korean keyword (\"골든크로스\"; ambiguous keywords return candidates).")]
        string name_or_id,
        [Description("Market filter: all (default), kospi, or kosdaq.")]
        string market = "all",
        [Description("Maximum matching rows to return (1-100). Default 20.")]
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        string key = (name_or_id ?? "").Trim();
        if (key.Length == 0)
            return McpJson.Error("name_or_id is required. Pass an exact catalog name, a 4-character search_cd, or a Korean keyword.");
        if (!TryResolveMarket(market, out string gubun, out string normalizedMarket))
            return McpJson.Error($"market '{market}' is not recognized. Use all, kospi, or kosdaq.");
        if (limit < 1 || limit > MaxRows)
            return McpJson.Error($"limit must be between 1 and {MaxRows}.", new { received = limit });

        try
        {
            IReadOnlyList<ScreenerInfo> catalog = await GetCatalogAsync(apiClient, cancellationToken).ConfigureAwait(false);
            ScreenerResolution resolution = ResolveSignal(catalog, key);

            if (resolution.Candidates.Count >= 2)
                return BuildAmbiguityResponse(
                    "ls_run_screener",
                    new[] { key },
                    resolved: Array.Empty<ScreenerInfo>(),
                    ambiguous: new Dictionary<string, IReadOnlyList<ScreenerInfo>>(StringComparer.Ordinal)
                    {
                        [key] = resolution.Candidates,
                    },
                    notFound: Array.Empty<string>(),
                    catalog);

            ScreenerInfo? screener = resolution.Single;
            if (screener is null)
                return McpJson.Error("Q-Click signal was not found in the LS catalog.", new
                {
                    name_or_id = key,
                    hint = "Call ls_list_screeners to see the 99-signal catalog (ids 6001-6412) or pass an exact name / 4-character search_cd. User-authored HTS [1892] conditions are a separate system and are not exposed here.",
                });

            LsTrResponse response = await apiClient.CallTrAsync(
                "t1825",
                new JsonObject
                {
                    ["search_cd"] = screener.Id,
                    ["gubun"] = gubun,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // LS quirk: t1825 returns rsp_cd="" on success (per official example).
            if (!IsScreenerSuccess(response, "t1825OutBlock1"))
                return McpJson.Error("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    source_tr = "t1825",
                    screener_id = screener.Id,
                });

            var rows = new List<ScreenerRow>();
            JsonElement? array = response.GetBlock("t1825OutBlock1");
            if (array is not null && array.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement row in array.Value.EnumerateArray())
                {
                    if (rows.Count >= limit)
                        break;
                    rows.Add(ParseRow(row, rows.Count + 1));
                }
            }

            DateEnvelope.TryResolveKrxDailySnapshot(null, out DateEnvelope dateEnvelope, out _);
            int totalAvailable = (int)(response.GetBlock("t1825OutBlock")?.ReadLong("JongCnt") ?? rows.Count);
            var payload = new ScreenerRunPayload
            {
                Screener = screener,
                Market = normalizedMarket,
                Count = rows.Count,
                TotalAvailable = Math.Max(totalAvailable, rows.Count),
                DataAsOf = dateEnvelope.DataAsOf,
                QueryDateResolution = dateEnvelope.QueryDateResolution,
                Results = rows,
                SourceTr = "t1825",
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
        catch (LsBusinessErrorException ex)
        {
            return McpJson.Error("LS reported a business-level error.", new
            {
                rsp_cd = ex.RspCode,
                rsp_msg = ex.RspMessage,
                source_tr = ex.SourceTr,
            });
        }
    }

    [McpServerTool(Name = "ls_combine_screeners")]
    [Description("""
        Runs multiple LS Q-Click signals and combines matched stocks by shcode set operation (AND intersection / OR union). This expresses compound screening that no single HTS screen offers — e.g. "골든크로스가 뜨면서 동시에 외인이 3일연속 순매수한 종목".

        USE WHEN: the user asks for stocks satisfying MULTIPLE Q-Click signals together ("AND") or ANY of a set ("OR"). Compound natural-language patterns: "A 이면서 B", "A + B + C 모두", "A 또는 B".
        AVOID WHEN: the user wants exactly one signal (use ls_run_screener); simple metric ranking (use ls_get_top_stocks / ls_get_fundamentals_rank); user-authored HTS conditions (out of scope).

        signals (2-8 entries): each entry is an exact catalog name (one of 99), a 4-character search_cd (6001-6412), or a Korean keyword ("골든크로스"). Server-side matched against the cached catalog. If any entry is ambiguous (matches multiple) or not found, the tool returns an envelope listing per-input candidates and the full mini-catalog of each ambiguous group (β policy), so the next call can pass exact ids without a separate ls_list_screeners round trip.
        mode: "and" intersection (default) keeps only stocks matched by EVERY listed signal. "or" union keeps stocks matched by AT LEAST ONE, deduplicated by shcode. Each result row carries signals_matched listing which signals fired for that stock. Ordering: AND preserves first-signal rank order (filtered to the intersection). OR sorts by descending signals_matched count first, then by best (smallest) rank — so when limit truncates, the higher-conviction "matched by multiple signals" stocks surface before single-signal matches.
        market: all / kospi / kosdaq. limit: max rows after combination (1-100, default 20). Results share the same envelope (shcode, name, price, change_pct, volume, data_as_of, query_date_resolution) as ls_run_screener for downstream chaining.
        """)]
    public static async Task<string> CombineScreeners(
        LsApiClient apiClient,
        [Description("2-8 Q-Click signals: exact names, 4-character search_cds, or Korean keywords. Ambiguous keywords get resolved via the catalog or returned as candidates.")]
        string[] signals,
        [Description("Combination mode: 'and' (intersection, default) or 'or' (union). 'intersection' / 'union' / '교집합' / '합집합' also accepted.")]
        string mode = "and",
        [Description("Market filter: all (default), kospi, or kosdaq.")]
        string market = "all",
        [Description("Max rows after combination (1-100). Default 20.")]
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (signals is null || signals.Length == 0)
            return McpJson.Error("signals is required. Provide 2-8 Q-Click signal names/ids/keywords.");
        var cleaned = signals.Select(s => (s ?? "").Trim()).Where(s => s.Length > 0).ToList();
        if (cleaned.Count < 2)
            return McpJson.Error("Provide at least 2 signals to combine. For a single signal, use ls_run_screener.", new { received = cleaned.Count });
        if (cleaned.Count > MaxSignalsPerCombination)
            return McpJson.Error($"Too many signals; at most {MaxSignalsPerCombination} supported per call.", new { received = cleaned.Count });

        if (!TryResolveCombineMode(mode, out string normalizedMode))
            return McpJson.Error("mode must be 'and' or 'or' (intersection/union/교집합/합집합 also accepted).", new { received = mode });
        if (!TryResolveMarket(market, out string gubun, out string normalizedMarket))
            return McpJson.Error($"market '{market}' is not recognized. Use all, kospi, or kosdaq.");
        if (limit < 1 || limit > MaxRows)
            return McpJson.Error($"limit must be between 1 and {MaxRows}.", new { received = limit });

        try
        {
            IReadOnlyList<ScreenerInfo> catalog = await GetCatalogAsync(apiClient, cancellationToken).ConfigureAwait(false);

            var resolved = new List<ScreenerInfo>();
            var ambiguous = new Dictionary<string, IReadOnlyList<ScreenerInfo>>(StringComparer.Ordinal);
            var notFound = new List<string>();
            foreach (string entry in cleaned)
            {
                ScreenerResolution res = ResolveSignal(catalog, entry);
                if (res.Single is not null)
                    resolved.Add(res.Single);
                else if (res.Candidates.Count >= 2)
                    ambiguous[entry] = res.Candidates;
                else
                    notFound.Add(entry);
            }

            if (ambiguous.Count > 0 || notFound.Count > 0)
                return BuildAmbiguityResponse("ls_combine_screeners", cleaned.ToArray(), resolved, ambiguous, notFound, catalog);

            // Run each signal and collect rows keyed by shcode.
            var perSignalRows = new List<Dictionary<string, ScreenerRow>>();
            for (int i = 0; i < resolved.Count; i++)
            {
                ScreenerInfo s = resolved[i];
                LsTrResponse response = await apiClient.CallTrAsync(
                    "t1825",
                    new JsonObject
                    {
                        ["search_cd"] = s.Id,
                        ["gubun"] = gubun,
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!IsScreenerSuccess(response, "t1825OutBlock1"))
                    return McpJson.Error("LS reported a business-level error while running a signal.", new
                    {
                        rsp_cd = response.RspCode,
                        rsp_msg = response.RspMessage,
                        source_tr = "t1825",
                        failed_signal_id = s.Id,
                        failed_signal_name = s.Name,
                    });

                var byShcode = new Dictionary<string, ScreenerRow>(StringComparer.Ordinal);
                JsonElement? array = response.GetBlock("t1825OutBlock1");
                if (array is not null && array.Value.ValueKind == JsonValueKind.Array)
                {
                    int rankInSignal = 1;
                    foreach (JsonElement row in array.Value.EnumerateArray())
                    {
                        ScreenerRow parsed = ParseRow(row, rankInSignal++);
                        string? code = parsed.Shcode;
                        if (string.IsNullOrEmpty(code)) continue;
                        byShcode[code] = parsed;
                    }
                }
                perSignalRows.Add(byShcode);
            }

            // Combine by shcode.
            // - AND: intersection of all per-signal shcode sets, ordered by
            //   first-signal rank so the user sees the most relevant matches first.
            // - OR: union, ordered by (a) descending signals_matched count so
            //   stocks confirmed by MORE signals surface first (most relevant
            //   compound matches), then (b) by first-signal rank within ties.
            //   This avoids the v1.4-dev observation where limit=10 in an
            //   asymmetric OR (e.g. 300 vs 2) buried the smaller signal's
            //   matches past the limit.
            IReadOnlyCollection<string> combinedShcodes = normalizedMode == "and"
                ? IntersectShcodes(perSignalRows)
                : UnionShcodesOrderedByMatchCount(perSignalRows);

            // Assemble row payloads, attaching the list of signals each stock matched.
            var combinedRows = new List<CombinedScreenerRow>();
            int rank = 1;
            foreach (string shcode in combinedShcodes)
            {
                ScreenerRow? sample = null;
                var matchedIds = new List<string>();
                for (int i = 0; i < resolved.Count; i++)
                {
                    if (perSignalRows[i].TryGetValue(shcode, out ScreenerRow? row))
                    {
                        sample ??= row;
                        matchedIds.Add(resolved[i].Id);
                    }
                }
                if (sample is null) continue;
                combinedRows.Add(new CombinedScreenerRow(
                    Rank: rank++,
                    Shcode: sample.Shcode,
                    Name: sample.Name,
                    Price: sample.Price,
                    Sign: sample.Sign,
                    ConsecutiveBars: sample.ConsecutiveBars,
                    Change: sample.Change,
                    ChangePct: sample.ChangePct,
                    Volume: sample.Volume,
                    VolumeRatePct: sample.VolumeRatePct,
                    SignalsMatched: matchedIds));
                if (combinedRows.Count >= limit) break;
            }

            DateEnvelope.TryResolveKrxDailySnapshot(null, out DateEnvelope envelope, out _);
            var payload = new CombineScreenersPayload
            {
                SignalsResolved = resolved.Select((s, i) => new SignalResolutionEntry(
                    Id: s.Id,
                    Name: s.Name,
                    Group: s.Group,
                    GroupLabel: s.GroupLabel,
                    MatchedCount: perSignalRows[i].Count)).ToList(),
                Mode = normalizedMode,
                Market = normalizedMarket,
                Count = combinedRows.Count,
                TotalInCombination = combinedShcodes.Count,
                DataAsOf = envelope.DataAsOf,
                QueryDateResolution = envelope.QueryDateResolution,
                Results = combinedRows,
                SourceTr = $"t1825 x {resolved.Count}",
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
        catch (LsBusinessErrorException ex)
        {
            return McpJson.Error("LS reported a business-level error.", new
            {
                rsp_cd = ex.RspCode,
                rsp_msg = ex.RspMessage,
                source_tr = ex.SourceTr,
            });
        }
    }

    /// <summary>
    /// Returns the process-cached LS Q-Click catalog. First call seeds the
    /// cache via <see cref="FetchScreenersAsync"/>; subsequent calls reuse it
    /// for the server lifetime. Tests can drop it via <see cref="ResetCatalogForTesting"/>.
    /// </summary>
    static async Task<IReadOnlyList<ScreenerInfo>> GetCatalogAsync(
        LsApiClient apiClient,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ScreenerInfo>? current = _catalog;
        if (current is not null)
            return current;

        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_catalog is not null)
                return _catalog;
            _catalog = await FetchScreenersAsync(apiClient, Groups, cancellationToken).ConfigureAwait(false);
            return _catalog;
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    /// <summary>
    /// Resolves a single user-facing query against the catalog.
    /// Resolution order: exact id (Ordinal) → exact name (case-insensitive) →
    /// normalized substring (whitespace/punctuation stripped, lowercased).
    /// A 4-character numeric id that misses the catalog still resolves so
    /// callers can target IDs not yet known to our snapshot.
    /// </summary>
    static ScreenerResolution ResolveSignal(IReadOnlyList<ScreenerInfo> catalog, string query)
    {
        string trimmed = (query ?? "").Trim();
        if (trimmed.Length == 0)
            return new ScreenerResolution(null, Array.Empty<ScreenerInfo>());

        ScreenerInfo? byId = catalog.FirstOrDefault(s => string.Equals(s.Id, trimmed, StringComparison.Ordinal));
        if (byId is not null)
            return new ScreenerResolution(byId, Array.Empty<ScreenerInfo>());

        ScreenerInfo? byName = catalog.FirstOrDefault(s =>
            s.Name is not null && string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
            return new ScreenerResolution(byName, Array.Empty<ScreenerInfo>());

        string needle = Normalize(trimmed);
        if (needle.Length > 0)
        {
            var matched = catalog
                .Where(s => s.Name is not null && Normalize(s.Name).Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matched.Count == 1)
                return new ScreenerResolution(matched[0], Array.Empty<ScreenerInfo>());
            if (matched.Count >= 2)
                return new ScreenerResolution(null, matched);
        }

        // Last-resort passthrough for 4-digit codes not present in our snapshot.
        if (IsSearchCode(trimmed))
            return new ScreenerResolution(new ScreenerInfo(trimmed, null, null, null), Array.Empty<ScreenerInfo>());

        return new ScreenerResolution(null, Array.Empty<ScreenerInfo>());
    }

    /// <summary>
    /// Builds the β-policy ambiguity envelope: per-input candidates plus, for
    /// each group with an ambiguous match, the full mini-catalog of that
    /// group so the model can pick a related signal without an extra
    /// ls_list_screeners round trip.
    /// </summary>
    static string BuildAmbiguityResponse(
        string toolName,
        string[] originalQueries,
        IReadOnlyList<ScreenerInfo> resolved,
        IReadOnlyDictionary<string, IReadOnlyList<ScreenerInfo>> ambiguous,
        IReadOnlyList<string> notFound,
        IReadOnlyList<ScreenerInfo> catalog)
    {
        HashSet<string> groupNames = new(StringComparer.Ordinal);
        foreach (var pair in ambiguous)
            foreach (ScreenerInfo c in pair.Value)
                if (c.Group is not null)
                    groupNames.Add(c.Group);

        var groupCatalogs = groupNames.ToDictionary(
            g => g,
            g => (object)catalog
                .Where(s => s.Group == g)
                .Select(s => new { id = s.Id, name = s.Name })
                .ToList());

        return McpJson.Error("Some Q-Click signals could not be resolved unambiguously.", new
        {
            tool = toolName,
            original = originalQueries,
            resolved = resolved.Select(s => new { id = s.Id, name = s.Name, group = s.Group, group_label = s.GroupLabel }),
            ambiguous = ambiguous.ToDictionary(
                kv => kv.Key,
                kv => (object)kv.Value.Select(s => new { id = s.Id, name = s.Name, group = s.Group, group_label = s.GroupLabel }).ToList()),
            not_found = notFound,
            group_catalogs = groupCatalogs,
            hint = "Re-call this tool with the exact name or 4-character id from candidates / group_catalogs above. group_catalogs lists every signal in each ambiguous group so a related signal can be chosen if the original keyword does not fit.",
        });
    }

    static ScreenerRow ParseRow(JsonElement row, int rank)
    {
        string? sign = row.ReadString("sign");
        return new ScreenerRow(
            Rank: rank,
            Shcode: row.ReadString("shcode")?.Trim(),
            Name: row.ReadString("hname")?.Trim(),
            Price: row.ReadLong("close"),
            Sign: sign,
            ConsecutiveBars: row.ReadLong("signcnt"),
            Change: (long)IndustryDataCache.ApplySign(row.ReadLong("change"), sign),
            ChangePct: IndustryDataCache.ApplySign(row.ReadDouble("diff"), sign),
            Volume: row.ReadLong("volume"),
            VolumeRatePct: row.ReadDouble("volumerate"));
    }

    static List<string> IntersectShcodes(IReadOnlyList<Dictionary<string, ScreenerRow>> perSignal)
    {
        if (perSignal.Count == 0)
            return new List<string>();

        // Preserve first-signal rank order, filtered to shcodes present in every signal.
        var common = new HashSet<string>(perSignal[0].Keys, StringComparer.Ordinal);
        for (int i = 1; i < perSignal.Count; i++)
            common.IntersectWith(perSignal[i].Keys);

        var ordered = new List<string>(common.Count);
        foreach (var kv in perSignal[0].OrderBy(kv => kv.Value.Rank))
            if (common.Contains(kv.Key))
                ordered.Add(kv.Key);
        return ordered;
    }

    static List<string> UnionShcodesOrderedByMatchCount(IReadOnlyList<Dictionary<string, ScreenerRow>> perSignal)
    {
        // For each shcode, count how many signals matched + remember the
        // best (smallest) rank across signals it appeared in. Then sort by
        // (matchCount DESC, bestRank ASC) so stocks confirmed by more signals
        // — the higher-conviction part of the union — surface first.
        var matchCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var bestRank = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var dict in perSignal)
        {
            foreach (var kv in dict)
            {
                string code = kv.Key;
                matchCount[code] = matchCount.TryGetValue(code, out int n) ? n + 1 : 1;
                int rank = kv.Value.Rank;
                if (!bestRank.TryGetValue(code, out int prev) || rank < prev)
                    bestRank[code] = rank;
            }
        }

        return matchCount.Keys
            .OrderByDescending(code => matchCount[code])
            .ThenBy(code => bestRank[code])
            .ToList();
    }

    static async Task<IReadOnlyList<ScreenerInfo>> FetchScreenersAsync(
        LsApiClient apiClient,
        IReadOnlyList<ScreenerGroup> groups,
        CancellationToken cancellationToken)
    {
        var results = new List<ScreenerInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScreenerGroup group in groups)
        {
            LsTrResponse response;
            try
            {
                response = await apiClient.CallTrAsync(
                    "t1826",
                    new JsonObject { ["search_gb"] = group.Code },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (LsTrException) when (group.Code == "4")
            {
                // search_gb=4 (급변종목) is not in the LS spec doc; if the
                // server rejects it, skip the group rather than failing the
                // entire catalog fetch.
                continue;
            }

            // LS quirk: t1826 returns rsp_cd="" on success (per official example).
            if (!IsScreenerSuccess(response, "t1826OutBlock"))
            {
                if (group.Code == "4")
                    continue;
                throw new LsBusinessErrorException("t1826", response.RspCode, response.RspMessage);
            }

            JsonElement? array = response.GetBlock("t1826OutBlock");
            if (array is null || array.Value.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement row in array.Value.EnumerateArray())
            {
                string id = row.ReadString("search_cd")?.Trim() ?? "";
                if (id.Length == 0 || !seen.Add(id))
                    continue;
                string? name = row.ReadString("search_nm")?.Trim();
                results.Add(new ScreenerInfo(
                    Id: id,
                    Name: string.IsNullOrWhiteSpace(name) ? null : name,
                    Group: group.Name,
                    GroupLabel: group.Label));
            }
        }
        return results;
    }

    static bool TryResolveGroups(string? raw, out IReadOnlyList<ScreenerGroup> groups, out string? error)
    {
        string normalized = NormalizeGroup(raw);
        if (normalized is "all")
        {
            groups = Groups;
            error = null;
            return true;
        }

        ScreenerGroup? group = Groups.FirstOrDefault(g =>
            string.Equals(g.Code, normalized, StringComparison.Ordinal)
            || string.Equals(g.Name, normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(g.Label, raw?.Trim(), StringComparison.Ordinal));
        if (group is null)
        {
            groups = Array.Empty<ScreenerGroup>();
            error = $"search_group '{raw}' is not recognized. Use all, core, indicator, market_trend, investor_trend, rapid_change, or 0/1/2/3/4.";
            return false;
        }

        groups = new[] { group };
        error = null;
        return true;
    }

    static string NormalizeGroup(string? raw)
    {
        string v = (raw ?? "all").Trim().ToLowerInvariant().Replace("-", "_");
        return v switch
        {
            "" or "all" or "전체" => "all",
            "핵심검색" => "core",
            "지표검색" => "indicator",
            "시세동향" => "market_trend",
            "투자자동향" => "investor_trend",
            "급변종목" or "rapid" => "rapid_change",
            _ => v,
        };
    }

    static bool TryResolveMarket(string? raw, out string code, out string normalized)
    {
        string lower = (raw ?? "all").Trim().ToLowerInvariant();
        switch (lower)
        {
            case "" or "all" or "전체":
                code = "0"; normalized = "all"; return true;
            case "kospi" or "코스피":
                code = "1"; normalized = "kospi"; return true;
            case "kosdaq" or "코스닥":
                code = "2"; normalized = "kosdaq"; return true;
            default:
                code = ""; normalized = ""; return false;
        }
    }

    static bool TryResolveCombineMode(string? raw, out string normalized)
    {
        string lower = (raw ?? "and").Trim().ToLowerInvariant();
        switch (lower)
        {
            case "and" or "intersection" or "교집합":
                normalized = "and"; return true;
            case "or" or "union" or "합집합":
                normalized = "or"; return true;
            default:
                normalized = ""; return false;
        }
    }

    static bool IsSearchCode(string value) =>
        value.Length == 4 && value.All(char.IsDigit);

    /// <summary>
    /// Success check tolerant of the LS t1825/t1826 quirk where a successful
    /// response carries <c>rsp_cd=""</c> instead of <c>"00000"</c>. Empty code
    /// is treated as success when the expected output block is present.
    /// </summary>
    static bool IsScreenerSuccess(LsTrResponse response, string expectedBlock)
    {
        if (response.IsSuccess)
            return true;
        if (!string.IsNullOrEmpty(response.RspCode))
            return false;
        return response.GetBlock(expectedBlock) is not null;
    }

    /// <summary>
    /// Normalizes a query string for substring matching: strips whitespace,
    /// common punctuation, and lowercases. So "골든크로스(5,20)" and
    /// "골든크로스 5 20" both normalize to "골든크로스520".
    /// </summary>
    static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c) || c is '(' or ')' or ',' or '_' or '-' or '/' or '.' or '[' or ']')
                continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    sealed class LsBusinessErrorException(string sourceTr, string? rspCode, string? rspMessage) : Exception
    {
        public string SourceTr { get; } = sourceTr;
        public string? RspCode { get; } = rspCode;
        public string? RspMessage { get; } = rspMessage;
    }

    sealed record ScreenerGroup(string Code, string Name, string Label);

    sealed record ScreenerInfo(
        string Id,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Group,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GroupLabel);

    sealed record ScreenerResolution(ScreenerInfo? Single, IReadOnlyList<ScreenerInfo> Candidates);

    sealed record ScreenerRunPayload
    {
        public ScreenerInfo Screener { get; init; } = null!;
        public string Market { get; init; } = "";
        public int Count { get; init; }
        public int TotalAvailable { get; init; }
        public string DataAsOf { get; init; } = "";
        public QueryDateResolution QueryDateResolution { get; init; }
        public IReadOnlyList<ScreenerRow> Results { get; init; } = Array.Empty<ScreenerRow>();
        public string SourceTr { get; init; } = "";
    }

    sealed record ScreenerRow(
        int Rank,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Shcode,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
        long Price,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sign,
        long ConsecutiveBars,
        long Change,
        double ChangePct,
        long Volume,
        double VolumeRatePct);

    sealed record SignalResolutionEntry(
        string Id,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Group,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GroupLabel,
        int MatchedCount);

    sealed record CombineScreenersPayload
    {
        public IReadOnlyList<SignalResolutionEntry> SignalsResolved { get; init; } = Array.Empty<SignalResolutionEntry>();
        public string Mode { get; init; } = "";
        public string Market { get; init; } = "";
        public int Count { get; init; }
        public int TotalInCombination { get; init; }
        public string DataAsOf { get; init; } = "";
        public QueryDateResolution QueryDateResolution { get; init; }
        public IReadOnlyList<CombinedScreenerRow> Results { get; init; } = Array.Empty<CombinedScreenerRow>();
        public string SourceTr { get; init; } = "";
    }

    sealed record CombinedScreenerRow(
        int Rank,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Shcode,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
        long Price,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sign,
        long ConsecutiveBars,
        long Change,
        double ChangePct,
        long Volume,
        double VolumeRatePct,
        IReadOnlyList<string> SignalsMatched);
}
