namespace AIUsageCounter;

/// <summary>
/// Renews the Claude Code login by starting claude.exe. The new expiry is
/// <c>claudeAiOauth.expiresAt</c> in ~/.claude/.credentials.json. Other entries in that
/// file, such as MCP tokens, are ignored.
/// </summary>
internal static class ClaudeCliRefresh
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static Task<bool> TryRenewAsync(string credentialsPath, CancellationToken ct = default) =>
        CliRefresh.RenewAsync(() => TryReadExpiry(credentialsPath), FindLaunch, Gate, ct);

    internal static CliRefresh.Launch? FindLaunch()
    {
        var candidates = new List<string>();
        foreach (string cmd in CliRefresh.PathExes("claude.cmd"))
        {
            if (!File.Exists(cmd)) continue;
            string? dir = Path.GetDirectoryName(cmd);
            if (dir is null) continue;
            candidates.Add(Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"));
        }
        candidates.AddRange(CliRefresh.PathExes("claude.exe"));
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(profile, ".local", "bin", "claude.exe"));
        candidates.Add(Path.Combine(profile, ".claude", "local", "claude.exe"));
        return CliRefresh.FirstLaunch(candidates);
    }

    internal static DateTimeOffset? TryReadExpiry(string credentialsPath)
    {
        try
        {
            using var creds = System.Text.Json.JsonDocument.Parse(File.ReadAllText(credentialsPath));
            if (!creds.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return null;
            if (!oauth.TryGetProperty("expiresAt", out var exp) || exp.ValueKind != System.Text.Json.JsonValueKind.Number)
                return null;
            if (!exp.TryGetInt64(out long ms)) return null;
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
