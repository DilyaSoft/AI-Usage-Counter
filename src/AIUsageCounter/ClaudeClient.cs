using System.Net.Http.Headers;
using System.Text.Json;

namespace AIUsageCounter;

public record UsageLimit(string Title, double Percent, DateTimeOffset? ResetsAt);

/// <summary>One provider block in the widget (Claude, Codex). Error is set when the fetch failed.</summary>
public record UsageSection(string Name, string? Plan, List<UsageLimit> Limits, string? Error = null);

public class UsageException(string message) : Exception(message);

/// <summary>
/// Reads the Claude Code OAuth token from ~/.claude/.credentials.json and queries
/// the same usage endpoint that Claude Code's /usage command uses.
/// When the token is rejected, <see cref="ClaudeCliRefresh"/> starts Claude Code and lets
/// that CLI write a new token. This class never calls the OAuth refresh endpoint itself.
/// </summary>
public static class ClaudeClient
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static string CredentialsPath =>
        Path.Combine(CliHome.Resolve("CLAUDE_CONFIG_DIR", ".claude"), ".credentials.json");

    public static bool IsConfigured => File.Exists(CredentialsPath);

    public static async Task<UsageSection> FetchAsync()
    {
        string credentialsPath = CredentialsPath;
        try
        {
            return await FetchAsync(credentialsPath, Http, DateTimeOffset.UtcNow);
        }
        catch (UsageException ex) when (CanRenew(ex))
        {
            if (!await ClaudeCliRefresh.TryRenewAsync(credentialsPath)) throw;
            return await FetchAsync(credentialsPath, Http, DateTimeOffset.UtcNow);
        }
    }

    private static bool CanRenew(UsageException ex) =>
        ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase)
        || ex.Message.StartsWith("401:", StringComparison.Ordinal);

    internal static async Task<UsageSection> FetchAsync(string credentialsPath, HttpClient http, DateTimeOffset now)
    {
        if (!File.Exists(credentialsPath))
            throw new UsageException("Claude Code credentials not found — sign in to Claude Code");

        using var creds = JsonDocument.Parse(await File.ReadAllTextAsync(credentialsPath));
        if (!creds.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
            !oauth.TryGetProperty("accessToken", out var tokenEl))
            throw new UsageException("No OAuth token in credentials — run /login in Claude Code");

        string token = tokenEl.GetString() ?? "";
        string? plan = oauth.TryGetProperty("subscriptionType", out var p) ? p.GetString() : null;

        if (oauth.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.Number &&
            DateTimeOffset.FromUnixTimeMilliseconds(exp.GetInt64()) < now)
            throw new UsageException("Token expired — run claude to refresh it");

        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        req.Headers.UserAgent.ParseAdd("ai-usage-counter/1.0");

        using var resp = await http.SendAsync(req);
        if ((int)resp.StatusCode == 401)
            throw new UsageException("401: token invalid — run claude");
        if (!resp.IsSuccessStatusCode)
            throw new UsageException($"HTTP {(int)resp.StatusCode}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return new UsageSection("Claude", plan, Parse(doc.RootElement));
    }

    internal static List<UsageLimit> Parse(JsonElement root)
    {
        var result = new List<UsageLimit>();

        // Newer response shape: a "limits" array with session / weekly / per-model entries.
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in limits.EnumerateArray())
            {
                string kind = Str(l, "kind") ?? "";
                string title = kind switch
                {
                    "session" => "Session (5h)",
                    "weekly_all" => "Week — all models",
                    "weekly_scoped" => "Week — " + (ScopeName(l) ?? "model"),
                    _ => kind,
                };
                result.Add(new UsageLimit(title, Num(l, "percent"), Date(l, "resets_at")));
            }
            if (result.Count > 0) return result;
        }

        // Older shape: five_hour / seven_day / seven_day_<model> objects.
        (string key, string title)[] known =
        [
            ("five_hour", "Session (5h)"),
            ("seven_day", "Week — all models"),
            ("seven_day_opus", "Week — Opus"),
            ("seven_day_sonnet", "Week — Sonnet"),
        ];
        foreach (var (key, title) in known)
        {
            if (root.TryGetProperty(key, out var o) && o.ValueKind == JsonValueKind.Object)
                result.Add(new UsageLimit(title, Num(o, "utilization"), Date(o, "resets_at")));
        }
        return result;
    }

    private static string? ScopeName(JsonElement l)
    {
        if (l.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            foreach (var part in new[] { "model", "surface" })
                if (s.TryGetProperty(part, out var m) && m.ValueKind == JsonValueKind.Object)
                    return Str(m, "display_name") ?? Str(m, "id");
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Str(e, name), out var d) ? d : null;
}
