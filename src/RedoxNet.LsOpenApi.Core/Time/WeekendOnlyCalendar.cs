namespace RedoxNet.LsOpenApi.Core.Time;

/// <summary>
/// v1.4 trading calendar: excludes Saturdays and Sundays only. Exchange holiday
/// tables can replace this behind <see cref="ITradingCalendar"/> later.
/// </summary>
public sealed class WeekendOnlyCalendar : ITradingCalendar
{
    /// <inheritdoc />
    public bool IsTradingDay(DateOnly date) =>
        date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday;

    /// <inheritdoc />
    public DateOnly LastTradingDayOnOrBefore(DateOnly date)
    {
        DateOnly cursor = date;
        while (!IsTradingDay(cursor))
            cursor = cursor.AddDays(-1);
        return cursor;
    }

    /// <inheritdoc />
    public DateOnly NextTradingDayOnOrAfter(DateOnly date)
    {
        DateOnly cursor = date;
        while (!IsTradingDay(cursor))
            cursor = cursor.AddDays(1);
        return cursor;
    }
}
