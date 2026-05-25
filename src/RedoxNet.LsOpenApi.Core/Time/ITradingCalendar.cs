namespace RedoxNet.LsOpenApi.Core.Time;

/// <summary>
/// Trading-day abstraction used by model-facing tools that need to state the
/// exact market date represented by a response.
/// </summary>
public interface ITradingCalendar
{
    /// <summary>Returns true when <paramref name="date"/> is a trading day.</summary>
    bool IsTradingDay(DateOnly date);

    /// <summary>Returns the latest trading day on or before <paramref name="date"/>.</summary>
    DateOnly LastTradingDayOnOrBefore(DateOnly date);

    /// <summary>Returns the next trading day on or after <paramref name="date"/>.</summary>
    DateOnly NextTradingDayOnOrAfter(DateOnly date);
}
