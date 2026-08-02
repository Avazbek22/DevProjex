namespace DevProjex.Application.Updates;

public static class ApplicationUpdateSchedule
{
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromDays(7);

    public static bool IsDue(
        bool automaticCheckEnabled,
        DateTimeOffset? lastCheckUtc,
        DateTimeOffset nowUtc)
    {
        if (!automaticCheckEnabled)
            return false;

        // A clock correction must not suppress checks indefinitely. Once a new attempt
        // is persisted with the corrected clock, the normal seven-day cadence resumes.
        return lastCheckUtc is null ||
               lastCheckUtc > nowUtc ||
               nowUtc - lastCheckUtc >= AutomaticCheckInterval;
    }
}
