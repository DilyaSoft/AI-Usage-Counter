namespace AIUsageCounter;

/// <summary>
/// One subscription the widget can show. A provider is "detected" when its CLI login file exists,
/// so every user only sees the services they are actually signed in to.
/// </summary>
public record UsageProvider(
    string Name,
    Color Color,
    string UsagePageUrl,
    Func<bool> IsDetected,
    Func<Task<UsageSection>> FetchAsync);

public static class Providers
{
    public static readonly IReadOnlyList<UsageProvider> All =
    [
        new("Claude", Color.FromArgb(217, 119, 87), "https://claude.ai/settings/usage",
            () => ClaudeClient.IsConfigured, ClaudeClient.FetchAsync),
        new("Codex", Color.FromArgb(16, 163, 127), "https://chatgpt.com/codex/settings/usage",
            () => CodexClient.IsConfigured, CodexClient.FetchAsync),
        new("Grok", Color.FromArgb(120, 150, 255), "https://grok.com/?_s=usage",
            () => GrokClient.IsConfigured, GrokClient.FetchAsync),
    ];

    /// <summary>Made-up numbers for --demo mode.</summary>
    public static List<UsageSection> DemoSections(DateTimeOffset now) =>
    [
        new("Claude", "max",
        [
            new("Session (5h)", 18, now.AddHours(2).AddMinutes(40)),
            new("Week — all models", 42, now.AddDays(4).AddHours(6)),
            new("Week — Opus", 76, now.AddDays(4).AddHours(6)),
        ]),
        new("Codex", "plus", [new("Session (5h)", 9, now.AddHours(3)), new("Week", 35, now.AddDays(2).AddHours(3))]),
        new("Grok", "SuperGrok", [new("Week", 93, now.AddDays(1).AddHours(4))]),
    ];

    /// <summary>Providers to query: detected on this machine and not hidden by the user.</summary>
    public static List<UsageProvider> Active(IEnumerable<UsageProvider> all, ICollection<string> hidden) =>
        all.Where(p => p.IsDetected() && !hidden.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();
}

/// <summary>Resolves a CLI's config directory, honoring its override environment variable.</summary>
public static class CliHome
{
    public static string Resolve(string envVar, string defaultDirName, Func<string, string?>? getEnv = null, string? userProfile = null)
    {
        string? overridden = (getEnv ?? Environment.GetEnvironmentVariable)(envVar);
        if (!string.IsNullOrWhiteSpace(overridden))
            return Environment.ExpandEnvironmentVariables(overridden.Trim());
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, defaultDirName);
    }
}
