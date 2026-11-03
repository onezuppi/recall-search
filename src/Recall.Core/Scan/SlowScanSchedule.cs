using System.Globalization;

namespace Recall.Core.Scan;

/// <summary>Daily time (local clock) at which the slow folders are scanned. Pure rules; the agent owns the timer.</summary>
public static class SlowScanSchedule
{
    public const string Default = "03:00";

    private static readonly string[] Formats = ["H:mm", "HH:mm"];

    /// <summary>Empty or invalid text means "no scheduled scan".</summary>
    public static bool TryParse(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>The stored form: "3:5" is not accepted, "3:05" becomes "03:05".</summary>
    public static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// True when the latest planned run (today at the time, or yesterday when it has not come yet) is newer than the last scan.
    /// A computer that was off at the planned time scans once at the next check.
    /// </summary>
    public static bool IsDue(TimeOnly at, DateTime? lastScan, DateTime now)
    {
        var planned = now.Date.Add(at.ToTimeSpan());
        if (planned > now) planned = planned.AddDays(-1);
        return lastScan is not { } last || last < planned;
    }
}
