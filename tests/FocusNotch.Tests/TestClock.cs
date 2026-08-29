using FocusNotch.Core.Time;

namespace FocusNotch.Tests;

/// <summary>
/// A clock the tests drive by hand. Nothing in the suite sleeps: time only moves when a test
/// advances it, which is what makes the timer assertions exact rather than flaky.
/// </summary>
public sealed class TestClock : IClock
{
    public TestClock(DateTime utcNow, TimeZoneInfo? timeZone = null)
    {
        UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        LocalTimeZone = timeZone ?? TimeZoneInfo.Utc;
    }

    public DateTime UtcNow { get; private set; }

    public TimeZoneInfo LocalTimeZone { get; set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);

    public void AdvanceSeconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));

    public void SetUtc(DateTime utc) => UtcNow = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
}
