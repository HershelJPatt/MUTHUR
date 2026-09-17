namespace Muthur.Server.Components.Shared;

public static class Format
{
    /// <summary>"12s", "7m", "3h", "4d" — compact age the way the board shows it.</summary>
    public static string Age(DateTimeOffset then, DateTimeOffset now)
    {
        var span = now - then;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
        return $"{(int)span.TotalDays}d";
    }

    /// <summary>"in 12m" for a future instant, "lapsed" once it has passed.</summary>
    public static string Until(DateTimeOffset when, DateTimeOffset now) =>
        when <= now ? "lapsed" : "in " + Age(now, now + (when - now));
}
