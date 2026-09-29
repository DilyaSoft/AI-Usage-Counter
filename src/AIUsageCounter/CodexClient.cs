using System.Net.Http.Headers;
using System.Text.Json;

namespace AIUsageCounter;

/// <summary>
/// Reads the Codex CLI ChatGPT login from ~/.codex/auth.json and queries the endpoint
/// Codex uses for its /status rate limits. As with Claude, the token is never refreshed
/// here (that would rotate Codex's refresh token); Codex refreshes it itself when it runs.
/// </summary>
public static class CodexClient
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static string AuthPath => Path.Combine(CliHome.Resolve("CODEX_HOME", ".codex"), "auth.json");

    public static bool IsConfigured => File.Exists(AuthPath);

    public static Task<UsageSection> FetchAsync() => FetchAsync(AuthPath, Http);

    internal static async Task<UsageSection> FetchAsync(string authPath, HttpClient http)
    {
        using var auth = JsonDocument.Parse(await File.ReadAllTextAsync(authPath));
        if (!auth.RootElement.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object ||
            !tokens.TryGetProperty("access_token", out var tokenEl))
            throw new UsageException("Codex is not signed in with ChatGPT — run codex login");

        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenEl.GetString());
        if (tokens.TryGetProperty("account_id", out var acc) && acc.GetString() is { Length: > 0 } accountId)
            req.Headers.Add("ChatGPT-Account-Id", accountId);
        req.Headers.UserAgent.ParseAdd("codex_cli_rs");

        using var resp = await http.SendAsync(req);
        if ((int)resp.StatusCode is 401 or 403)
            throw new UsageException($"{(int)resp.StatusCode}: Codex token invalid — run codex");
        if (!resp.IsSuccessStatusCode)
            throw new UsageException($"Codex HTTP {(int)resp.StatusCode}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return Parse(doc.RootElement);
    }

    internal static UsageSection Parse(JsonElement root)
    {
        string? plan = root.TryGetProperty("plan_type", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        var limits = new List<UsageLimit>();
        AddRateLimit(limits, root, "rate_limit", null);
        AddRateLimit(limits, root, "code_review_rate_limit", "Code review");

        if (root.TryGetProperty("additional_rate_limits", out var extra) && extra.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in extra.EnumerateArray())
            {
                string? name = x.TryGetProperty("limit_name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() : null;
                AddRateLimit(limits, x, "rate_limit", name ?? "Extra limit");
            }
        }

        return new UsageSection("Codex", plan, limits);
    }

    private static void AddRateLimit(List<UsageLimit> limits, JsonElement parent, string prop, string? prefix)
    {
        if (!parent.TryGetProperty(prop, out var rl) || rl.ValueKind != JsonValueKind.Object) return;
        foreach (var window in new[] { "primary_window", "secondary_window" })
        {
            if (!rl.TryGetProperty(window, out var w) || w.ValueKind != JsonValueKind.Object) continue;

            double pct = w.TryGetProperty("used_percent", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : 0;
            long seconds = w.TryGetProperty("limit_window_seconds", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
            DateTimeOffset? reset = w.TryGetProperty("reset_at", out var r) && r.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(r.GetInt64()) : null;

            string title = WindowName(seconds);
            limits.Add(new UsageLimit(prefix is null ? title : $"{prefix} — {title}", pct, reset));
        }
    }

    internal static string WindowName(long seconds) => seconds switch
    {
        18_000 => "Session (5h)",
        604_800 => "Week",
        >= 86_400 when seconds % 86_400 == 0 => $"Window {seconds / 86_400}d",
        > 0 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Window {seconds / 3600.0:0.#}h"),
        _ => "Limit",
    };
}
