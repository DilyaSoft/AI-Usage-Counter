namespace AIUsageCounter;

/// <summary>Renews the Grok login by starting the Grok CLI. See <see cref="CliRefresh"/>.</summary>
internal static class GrokCliRefresh
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static Task<bool> TryRenewAsync(string authPath, CancellationToken ct = default) =>
        CliRefresh.RenewAsync(() => TryReadExpiry(authPath), FindLaunch, Gate, ct);

    internal static Task<bool> TryRenewAsync(
        string authPath,
        string? grokExe,
        Func<string, IChildProcess> start,
        DateTimeOffset now,
        TimeSpan timeout,
        TimeSpan poll,
        CancellationToken ct = default) =>
        CliRefresh.TryRenewAsync(
            () => TryReadExpiry(authPath),
            string.IsNullOrWhiteSpace(grokExe) ? null : new CliRefresh.Launch(grokExe),
            launch => start(launch.FileName),
            now, timeout, poll, ct);

    internal static CliRefresh.Launch? FindLaunch()
    {
        string? exe = FindGrokExe();
        return exe is null ? null : new CliRefresh.Launch(exe);
    }

    internal static string? FindGrokExe()
    {
        string home = CliHome.Resolve("GROK_HOME", ".grok");
        return CliRefresh.FirstLaunch(
        [
            Path.Combine(home, "bin", "grok.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "bin", "grok.exe"),
            .. CliRefresh.PathExes("grok.exe"),
        ])?.FileName;
    }

    internal static string? FindGrokExe(IEnumerable<string> candidates) =>
        CliRefresh.FirstLaunch(candidates)?.FileName;

    /// <summary>First issuer entry's expires_at. Null when the file is missing, locked, or unreadable.</summary>
    internal static DateTimeOffset? TryReadExpiry(string authPath)
    {
        try
        {
            using var auth = System.Text.Json.JsonDocument.Parse(File.ReadAllText(authPath));
            foreach (var prop in auth.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!prop.Value.TryGetProperty("expires_at", out var exp) || exp.ValueKind != System.Text.Json.JsonValueKind.String)
                    continue;
                if (DateTimeOffset.TryParse(exp.GetString(), out var expiresAt)) return expiresAt;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
        }
        return null;
    }
}
