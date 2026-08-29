namespace FocusNotch.Core.Time;

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTime UtcNow => DateTime.UtcNow;

    public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
}
