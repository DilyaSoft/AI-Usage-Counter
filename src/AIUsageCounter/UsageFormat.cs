using System.Globalization;

namespace AIUsageCounter;

public enum Severity { Normal, Warning, Critical }

/// <summary>UI-independent display logic for the widget, kept separate so it can be unit tested.</summary>
public static class UsageFormat
{
    public const double WarningPercent = 70;
    public const double CriticalPercent = 90;

    public static double Clamp(double percent) => Math.Clamp(percent, 0, 100);

    public static Severity SeverityOf(double percent) => Clamp(percent) switch
    {
        >= CriticalPercent => Severity.Critical,
        >= WarningPercent => Severity.Warning,
        _ => Severity.Normal,
    };

    public static string PercentText(double percent)
    {
        double used = Clamp(percent);
        return string.Create(CultureInfo.InvariantCulture, $"{used:0}% · {100 - used:0}% left");
    }

    /// <summary>"11:20 (in 42m)" for today, "Sun 04.10 16:00 (in 5d 5h)" otherwise; both times are local.</summary>
    public static string FormatReset(DateTime localReset, DateTime now)
    {
        var left = localReset - now;
        string when = localReset.Date == now.Date
            ? localReset.ToString("HH:mm", CultureInfo.InvariantCulture)
            : localReset.ToString("ddd dd.MM HH:mm", CultureInfo.InvariantCulture);
        if (left <= TimeSpan.Zero) return when;
        string rel = left.TotalDays >= 1 ? $"{(int)left.TotalDays}d {left.Hours}h"
                   : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m"
                   : $"{Math.Max(1, left.Minutes)}m";
        return $"{when} (in {rel})";
    }

    /// <summary>Runs one provider fetch; on failure keeps its last known limits and attaches the error.</summary>
    public static async Task<UsageSection> FetchSafeAsync(string name, Func<Task<UsageSection>> fetch, UsageSection? previous)
    {
        string error;
        try { return await fetch(); }
        catch (UsageException ex) { error = ex.Message; }
        catch (HttpRequestException ex) { error = "Network: " + ex.Message; }
        catch (TaskCanceledException) { error = "Request timed out"; }
        catch (Exception ex) { error = ex.Message; }

        return (previous ?? new UsageSection(name, null, [])) with { Error = error };
    }
}
