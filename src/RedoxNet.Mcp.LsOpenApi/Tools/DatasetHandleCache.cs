using System.Security.Cryptography;
using System.Text.Json;
using RedoxNet.LsOpenApi.Core.Indicators;
using RedoxNet.LsOpenApi.Core.Models;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// Bounded process-local cache for chart datasets referenced by opaque handles.
/// This is intentionally small; handles are short-lived affordances for follow-up
/// tool calls, not durable storage. In the current stdio server this cache is
/// process-wide, not a true per-chat-session store.
/// </summary>
internal static class DatasetHandleCache
{
    const int MaxDatasets = 16;
    const int MaxBytesPerDataset = 5 * 1024 * 1024;

    static readonly object Gate = new();
    static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds a dataset and returns its opaque handle.
    /// </summary>
    public static string Add(ChartDataset dataset)
    {
        string id = "ds_" + RandomNumberGenerator.GetHexString(8).ToLowerInvariant();
        int bytes = JsonSerializer.SerializeToUtf8Bytes(dataset, McpJson.Tool).Length;
        if (bytes > MaxBytesPerDataset)
            throw new InvalidOperationException($"Chart dataset is too large to cache ({bytes} bytes).");

        lock (Gate)
        {
            Entries[id] = new Entry(dataset, DateTimeOffset.UtcNow, bytes);
            EvictIfNeeded();
        }
        return id;
    }

    /// <summary>
    /// Tries to resolve a dataset by handle and updates its LRU timestamp.
    /// </summary>
    public static bool TryGet(string id, out ChartDataset? dataset)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(id, out Entry? entry))
            {
                dataset = null;
                return false;
            }

            Entries[id] = entry with { LastAccessUtc = DateTimeOffset.UtcNow };
            dataset = entry.Dataset;
            return true;
        }
    }

    /// <summary>
    /// Replaces a dataset under an existing handle and refreshes its LRU
    /// timestamp.
    /// </summary>
    public static bool TryUpdate(string id, ChartDataset dataset)
    {
        int bytes = JsonSerializer.SerializeToUtf8Bytes(dataset, McpJson.Tool).Length;
        if (bytes > MaxBytesPerDataset)
            throw new InvalidOperationException($"Chart dataset is too large to cache ({bytes} bytes).");

        lock (Gate)
        {
            if (!Entries.ContainsKey(id))
                return false;

            Entries[id] = new Entry(dataset, DateTimeOffset.UtcNow, bytes);
            EvictIfNeeded();
            return true;
        }
    }

    static void EvictIfNeeded()
    {
        while (Entries.Count > MaxDatasets)
        {
            string oldest = Entries
                .OrderBy(kv => kv.Value.LastAccessUtc)
                .First()
                .Key;
            Entries.Remove(oldest);
        }
    }

    sealed record Entry(ChartDataset Dataset, DateTimeOffset LastAccessUtc, int ByteSize);
}

/// <summary>
/// Internal dataset payload held behind a <c>dataset_id</c>.
/// </summary>
internal sealed record ChartDataset(
    string Shcode,
    string? Name,
    IReadOnlyList<string> PeriodTypes,
    IReadOnlyList<ChartDatasetFrame> Frames,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// One cached chart frame.
/// </summary>
internal sealed record ChartDatasetFrame(
    string PeriodType,
    string TrCode,
    int MinuteUnit,
    string? SourceFrom,
    string? SourceTo,
    IReadOnlyList<Candle> Candles,
    IReadOnlyDictionary<string, IReadOnlyList<double?>> Indicators,
    IReadOnlyList<IndicatorSpec> IndicatorSpecs,
    ChartContext Context,
    AnalyticalSummary Summary);
