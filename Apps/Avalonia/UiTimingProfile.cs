namespace DevProjex.Avalonia;

internal static class UiTimingProfile
{
    private const string FastTimingsEnvironmentVariable = "DEVPROJEX_FAST_UI_TESTS";
    private const double FastTimingScale = 0.08;
    private static readonly bool FastTimingsEnabled =
        string.Equals(
            Environment.GetEnvironmentVariable(FastTimingsEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

    public static bool AreFastTimingsEnabled => FastTimingsEnabled;

    public static TimeSpan AnimationSettleBuffer =>
        FastTimingsEnabled
            ? TimeSpan.FromMilliseconds(4)
            : TimeSpan.FromMilliseconds(24);

    // Headless UI tests validate layout/state transitions, not human-facing animation pacing.
    // Shortening these timings keeps the suite fast without changing production behavior.
    public static TimeSpan Scale(TimeSpan duration)
    {
        if (!FastTimingsEnabled || duration <= TimeSpan.Zero)
            return duration;

        var scaledMilliseconds = Math.Max(
            1,
            (int)Math.Round(duration.TotalMilliseconds * FastTimingScale, MidpointRounding.AwayFromZero));

        return TimeSpan.FromMilliseconds(scaledMilliseconds);
    }
}
