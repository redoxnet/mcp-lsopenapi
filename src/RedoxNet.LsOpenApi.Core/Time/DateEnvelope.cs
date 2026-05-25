using System.Globalization;

namespace RedoxNet.LsOpenApi.Core.Time;

/// <summary>Resolution reason for a model-facing market data date.</summary>
public enum QueryDateResolution
{
    Used,
    Weekend,
    Holiday,
    FutureDate,
    PreMarket,
}

/// <summary>
/// Standard top-level date envelope for tools that return date-bearing market
/// data. The MCP serializer emits this as data_as_of + query_date_resolution.
/// </summary>
public sealed record DateEnvelope(string DataAsOf, QueryDateResolution QueryDateResolution)
{
    public static readonly TimeZoneInfo SeoulTimeZone = ResolveTimeZone(
        iana: "Asia/Seoul",
        windows: "Korea Standard Time",
        fallbackId: "RedoxNet.KST",
        fallbackOffset: TimeSpan.FromHours(9));

    static readonly TimeOnly KrxOpen = new(9, 0);

    /// <summary>
    /// Resolves an optional yyyyMMdd query date into the market date a Korean
    /// daily-snapshot response should represent.
    /// </summary>
    public static bool TryResolveKrxDailySnapshot(
        string? queryDate,
        out DateEnvelope envelope,
        out string? error,
        ITradingCalendar? calendar = null,
        DateTimeOffset? now = null)
    {
        calendar ??= new WeekendOnlyCalendar();
        DateTimeOffset seoulNow = TimeZoneInfo.ConvertTime(now ?? DateTimeOffset.UtcNow, SeoulTimeZone);
        DateOnly today = DateOnly.FromDateTime(seoulNow.DateTime);
        TimeOnly currentTime = TimeOnly.FromDateTime(seoulNow.DateTime);

        string trimmed = (queryDate ?? "").Trim();
        if (trimmed.Length > 0)
        {
            if (!TryParseYmd(trimmed, out DateOnly requested))
            {
                envelope = default!;
                error = "query_date must use yyyyMMdd format, e.g. 20260522.";
                return false;
            }

            envelope = requested > today
                ? new DateEnvelope(ToYmd(CurrentAvailableTradingDay(today, currentTime, calendar)), QueryDateResolution.FutureDate)
                : ResolveExplicitQueryDate(requested, calendar);
            error = null;
            return true;
        }

        envelope = ResolveImplicitQueryDate(today, currentTime, calendar);
        error = null;
        return true;
    }

    /// <summary>Builds an envelope when the response row already carries the as-of date.</summary>
    public static DateEnvelope FromDataAsOf(string dataAsOf) =>
        new(dataAsOf, QueryDateResolution.Used);

    static DateEnvelope ResolveExplicitQueryDate(DateOnly requested, ITradingCalendar calendar)
    {
        if (calendar.IsTradingDay(requested))
            return new DateEnvelope(ToYmd(requested), QueryDateResolution.Used);

        DateOnly dataAsOf = calendar.LastTradingDayOnOrBefore(requested);
        QueryDateResolution resolution = requested.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            ? QueryDateResolution.Weekend
            : QueryDateResolution.Holiday;
        return new DateEnvelope(ToYmd(dataAsOf), resolution);
    }

    static DateEnvelope ResolveImplicitQueryDate(DateOnly today, TimeOnly currentTime, ITradingCalendar calendar)
    {
        if (!calendar.IsTradingDay(today))
        {
            QueryDateResolution resolution = today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? QueryDateResolution.Weekend
                : QueryDateResolution.Holiday;
            return new DateEnvelope(ToYmd(calendar.LastTradingDayOnOrBefore(today)), resolution);
        }

        if (currentTime < KrxOpen)
            return new DateEnvelope(ToYmd(calendar.LastTradingDayOnOrBefore(today.AddDays(-1))), QueryDateResolution.PreMarket);

        return new DateEnvelope(ToYmd(today), QueryDateResolution.Used);
    }

    static DateOnly CurrentAvailableTradingDay(DateOnly today, TimeOnly currentTime, ITradingCalendar calendar)
    {
        if (!calendar.IsTradingDay(today))
            return calendar.LastTradingDayOnOrBefore(today);
        return currentTime < KrxOpen
            ? calendar.LastTradingDayOnOrBefore(today.AddDays(-1))
            : today;
    }

    static bool TryParseYmd(string raw, out DateOnly parsed)
    {
        if (raw.Length == 8
            && DateOnly.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            return true;

        parsed = default;
        return false;
    }

    static string ToYmd(DateOnly date) =>
        date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    static TimeZoneInfo ResolveTimeZone(string iana, string windows, string fallbackId, TimeSpan fallbackOffset)
    {
        foreach (string id in new[] { iana, windows })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(fallbackId, fallbackOffset, fallbackId, fallbackId);
    }
}
