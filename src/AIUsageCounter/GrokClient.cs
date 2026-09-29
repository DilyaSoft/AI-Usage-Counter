using System.Net.Http.Headers;
using System.Text.Json;

namespace AIUsageCounter;

/// <summary>
/// Reads the Grok Build CLI login from ~/.grok/auth.json and queries the billing endpoint
/// the CLI uses for its /usage modal. The token is short-lived and refreshed only by the
/// Grok CLI itself; it is never refreshed here.
/// </summary>
public static class GrokClient
{
    private const string BillingUrl = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static string GrokDir => CliHome.Resolve("GROK_HOME", ".grok");

    private static string AuthPath => Path.Combine(GrokDir, "auth.json");

    public static bool IsConfigured => File.Exists(AuthPath);

    public static Task<UsageSection> FetchAsync() =>
        FetchAsync(AuthPath, Path.Combine(GrokDir, "settings_cache.json"), Http, DateTimeOffset.UtcNow);

    internal static async Task<UsageSection> FetchAsync(string authPath, string settingsCachePath, HttpClient http, DateTimeOffset now)
    {
        using var auth = JsonDocument.Parse(await File.ReadAllTextAsync(authPath));

        // auth.json is keyed by issuer ("https://auth.x.ai::<client id>"); take the first entry with a key.
        JsonElement? entry = null;
        foreach (var prop in auth.RootElement.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("key", out _))
            {
                entry = prop.Value;
                break;
            }
        if (entry is not JsonElement e || e.GetProperty("key").GetString() is not { Length: > 0 } token)
            throw new UsageException("Grok is not signed in — run grok login");

        if (e.TryGetProperty("expires_at", out var exp) && DateTimeOffset.TryParse(exp.GetString(), out var expiresAt) &&
            expiresAt < now)
            throw new UsageException("Grok token expired — run grok to refresh it");

        using var req = new HttpRequestMessage(HttpMethod.Get, BillingUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req);
        if ((int)resp.StatusCode is 401 or 403)
            throw new UsageException($"{(int)resp.StatusCode}: Grok token invalid — run grok");
        if (!resp.IsSuccessStatusCode)
            throw new UsageException($"Grok HTTP {(int)resp.StatusCode}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return new UsageSection("Grok", ReadPlan(settingsCachePath), Parse(doc.RootElement));
    }

    internal static List<UsageLimit> Parse(JsonElement root)
    {
        var config = root.TryGetProperty("config", out var c) ? c : root;

        DateTimeOffset? reset = null;
        string title = "Period";
        if (config.TryGetProperty("currentPeriod", out var period) && period.ValueKind == JsonValueKind.Object)
        {
            if (DateTimeOffset.TryParse(Str(period, "end"), out var end)) reset = end;
            title = Str(period, "type") switch
            {
                "USAGE_PERIOD_TYPE_WEEKLY" => "Week",
                "USAGE_PERIOD_TYPE_DAILY" => "Day",
                "USAGE_PERIOD_TYPE_MONTHLY" => "Month",
                _ => "Period",
            };
        }

        var limits = new List<UsageLimit> { new(title, Num(config, "creditUsagePercent"), reset) };

        // Per-product breakdown is only worth showing when there is more than one product.
        if (config.TryGetProperty("productUsage", out var products) && products.ValueKind == JsonValueKind.Array &&
            products.GetArrayLength() > 1)
        {
            foreach (var p in products.EnumerateArray())
                limits.Add(new UsageLimit($"{title} — {Str(p, "product") ?? "?"}", Num(p, "usagePercent"), reset));
        }

        return limits;
    }

    /// <summary>Best effort: the CLI caches the display name of the subscription tier in settings_cache.json.</summary>
    internal static string? ReadPlan(string settingsCachePath)
    {
        try
        {
            using var cache = JsonDocument.Parse(File.ReadAllText(settingsCachePath));
            if (Str(cache.RootElement, "payload") is not string payload) return null;
            using var inner = JsonDocument.Parse(payload);
            return FindString(inner.RootElement, "subscription_tier_display");
        }
        catch { return null; }
    }

    private static string? FindString(JsonElement e, string name)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == name && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
                    if (FindString(p.Value, name) is string s) return s;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    if (FindString(item, name) is string s) return s;
                break;
        }
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
