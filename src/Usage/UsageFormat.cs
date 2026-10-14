using System.Globalization;

namespace Helide.Usage;

/// <summary>How usage figures are written for people: "48.2M", "3h 12m", "~$41.80".</summary>
internal static class UsageFormat
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Tokens(long count) => count switch
    {
        < 1_000 => count.ToString(Invariant),
        < 10_000 => (count / 1_000d).ToString("0.0", Invariant) + "K",
        < 1_000_000 => (count / 1_000).ToString(Invariant) + "K",
        < 10_000_000 => (count / 1_000_000d).ToString("0.00", Invariant) + "M",
        < 1_000_000_000 => (count / 1_000_000d).ToString("0.0", Invariant) + "M",
        _ => (count / 1_000_000_000d).ToString("0.00", Invariant) + "B",
    };

    public static string Count(long count) => count.ToString("N0", Invariant);

    /// <summary>The two largest units: "2d 4h", "3h 12m", "45m", "38s".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1)
            return $"{(int)span.TotalMinutes}m";
        return $"{(int)span.TotalSeconds}s";
    }

    /// <summary>An estimate, so it says so with a tilde.</summary>
    public static string Cost(decimal dollars) => dollars switch
    {
        0 => "$0",
        < 0.01m => "<$0.01",
        < 1000m => "~$" + dollars.ToString("0.00", Invariant),
        _ => "~$" + dollars.ToString("N0", Invariant),
    };

    /// <summary>A figure's age: "just now", "read 3h ago".</summary>
    public static string Age(DateTimeOffset readAt, DateTimeOffset now)
    {
        if (readAt == DateTimeOffset.MinValue)
            return "age unknown";

        var age = now - readAt;
        return age < TimeSpan.FromMinutes(1) ? "read just now" : $"read {Duration(age)} ago";
    }
}
