namespace Sitim.Web.Services;

/// <summary>
/// Renders durations as <c>HH:mm:ss</c> regardless of source (millisecond count or
/// pair of timestamps). Centralizes display logic so every page that shows a job
/// runtime — finished or running — produces the same format.
/// </summary>
public static class DurationFormatter
{
    /// <summary>Returned when no duration can be computed.</summary>
    public const string Placeholder = "—";

    /// <summary>Format a nullable millisecond count as <c>HH:mm:ss</c>.</summary>
    public static string Format(int? milliseconds)
    {
        if (!milliseconds.HasValue || milliseconds.Value < 0)
            return Placeholder;
        return Format(TimeSpan.FromMilliseconds(milliseconds.Value));
    }

    /// <summary>Format a non-nullable millisecond count as <c>HH:mm:ss</c>.</summary>
    public static string Format(int milliseconds) =>
        milliseconds < 0 ? Placeholder : Format(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>Format a long millisecond count as <c>HH:mm:ss</c>.</summary>
    public static string Format(long milliseconds) =>
        milliseconds < 0 ? Placeholder : Format(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>Format a <see cref="TimeSpan"/> as <c>HH:mm:ss</c> (always with hours).</summary>
    public static string Format(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        return $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
    }

    /// <summary>
    /// Picks the best duration source based on job lifecycle:
    /// <list type="bullet">
    /// <item>Finished job with recorded <paramref name="finalMs"/> → that exact value (immutable truth).</item>
    /// <item>Started but not finished → live <c>UtcNow − startedUtc</c>.</item>
    /// <item>Not started → <see cref="Placeholder"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="nowUtc">Override the wall clock (mainly for testing).</param>
    public static string FormatLive(
        DateTime? startedUtc,
        DateTime? finishedUtc,
        int? finalMs,
        DateTime? nowUtc = null)
    {
        // Final recorded value wins — server is the source of truth once the job ends
        if (finishedUtc.HasValue && finalMs.HasValue && finalMs.Value >= 0)
            return Format(finalMs);

        if (!startedUtc.HasValue)
            return Placeholder;

        var now = nowUtc ?? DateTime.UtcNow;
        var ended = finishedUtc ?? now;
        return Format(ended - startedUtc.Value);
    }
}
