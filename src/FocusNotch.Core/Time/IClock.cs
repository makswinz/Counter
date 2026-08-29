namespace FocusNotch.Core.Time;

/// <summary>
/// Abstraction over "now" so timer and streak logic can be tested without sleeping.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }

    TimeZoneInfo LocalTimeZone { get; }
}

public static class ClockExtensions
{
    public static DateTime ToLocal(this IClock clock, DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), clock.LocalTimeZone);

    public static DateOnly Today(this IClock clock)
        => DateOnly.FromDateTime(clock.ToLocal(clock.UtcNow));
}
