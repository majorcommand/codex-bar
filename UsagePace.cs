namespace CodexBar;

internal sealed record UsagePace(
    double DaysUntilReset,
    double? AveragePerDay,
    double? DaysOfCapacity,
    double? RemainingAtReset,
    bool AbovePace,
    bool Exhausted)
{
    // The API supplies a seven-day window and its end, not daily history.

    public static UsagePace? Calculate(double usedPercent, DateTimeOffset resetsAt,
        DateTimeOffset observedAt, bool previouslyAbovePace = false)
    {
        var daysLeft = (resetsAt - observedAt).TotalDays;
        if (!double.IsFinite(usedPercent) || usedPercent is < 0 or > 100 ||
            daysLeft is <= 0 or > 7)
            return null;

        var elapsed = (TimeSpan.FromDays(7) - (resetsAt - observedAt)).TotalDays;
        var exhausted = usedPercent >= 100;
        // At the exact cycle start there is no elapsed time to divide by.
        if (elapsed <= 0)
            return new UsagePace(daysLeft, null, exhausted ? 0 : null, null, exhausted, exhausted);

        var average = usedPercent / elapsed;
        var projectedRemaining = 100 - average * 7;
        // A small hysteresis band prevents rounded API readings flickering the face.
        var abovePace = exhausted || (previouslyAbovePace
            ? projectedRemaining < 0.5
            : projectedRemaining < -0.5);
        return new UsagePace(daysLeft, average,
            average > 0 ? (100 - usedPercent) / average : null,
            projectedRemaining, abovePace, exhausted);
    }
}
