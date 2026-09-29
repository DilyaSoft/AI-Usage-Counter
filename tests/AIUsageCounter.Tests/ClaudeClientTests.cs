using System.Net;

namespace AIUsageCounter.Tests;

public class ClaudeClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 7, 30, 0, TimeSpan.Zero);

    private const string LimitsResponse = """
    {
      "five_hour": { "utilization": 3.0, "resets_at": "2026-09-29T11:19:59.55+03:00" },
      "seven_day": { "utilization": 54.0, "resets_at": "2026-10-04T15:59:59.55+03:00" },
      "limits": [
        { "kind": "session", "percent": 3, "resets_at": "2026-09-29T11:19:59.55+03:00", "scope": null },
        { "kind": "weekly_all", "percent": 54, "resets_at": "2026-10-04T15:59:59.55+03:00", "scope": null },
        { "kind": "weekly_scoped", "percent": 70, "resets_at": "2026-10-04T15:59:59.55+03:00",
          "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null } }
      ]
    }
    """;

    private static string Credentials(long? expiresAtMs = null, string plan = "max") => $$"""
    {
      "claudeAiOauth": {
        "accessToken": "sk-ant-oat-test",
        "refreshToken": "refresh",
        "expiresAt": {{expiresAtMs ?? Now.AddHours(1).ToUnixTimeMilliseconds()}},
        "subscriptionType": "{{plan}}"
      }
    }
    """;

    [Fact]
    public void Parse_LimitsArray_MapsKindsToTitles()
    {
        var limits = ClaudeClient.Parse(Json.Parse(LimitsResponse));

        Assert.Collection(limits,
            l => { Assert.Equal("Session (5h)", l.Title); Assert.Equal(3, l.Percent); },
            l => { Assert.Equal("Week — all models", l.Title); Assert.Equal(54, l.Percent); },
            l => { Assert.Equal("Week — Fable", l.Title); Assert.Equal(70, l.Percent); });
    }

    [Fact]
    public void Parse_KeepsResetOffset()
    {
        var limits = ClaudeClient.Parse(Json.Parse(LimitsResponse));
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 8, 19, 59, 550, TimeSpan.Zero), limits[0].ResetsAt!.Value.ToUniversalTime());
    }

    [Fact]
    public void Parse_ScopedWithoutName_FallsBackToModel()
    {
        var limits = ClaudeClient.Parse(Json.Parse("""
            { "limits": [ { "kind": "weekly_scoped", "percent": 5, "scope": null } ] }
            """));
        Assert.Equal("Week — model", limits.Single().Title);
        Assert.Null(limits.Single().ResetsAt);
    }

    [Fact]
    public void Parse_UnknownKind_UsesRawKind()
    {
        var limits = ClaudeClient.Parse(Json.Parse("""{ "limits": [ { "kind": "monthly_x", "percent": 1 } ] }"""));
        Assert.Equal("monthly_x", limits.Single().Title);
    }

    [Fact]
    public void Parse_LegacyShape_WithoutLimitsArray()
    {
        var limits = ClaudeClient.Parse(Json.Parse("""
            {
              "five_hour": { "utilization": 12.5, "resets_at": "2026-09-29T11:00:00Z" },
              "seven_day": { "utilization": 40, "resets_at": null },
              "seven_day_opus": { "utilization": 80, "resets_at": "2026-10-04T00:00:00Z" },
              "seven_day_sonnet": null
            }
            """));

        Assert.Equal(new[] { "Session (5h)", "Week — all models", "Week — Opus" }, limits.Select(l => l.Title));
        Assert.Equal(12.5, limits[0].Percent);
        Assert.Null(limits[1].ResetsAt);
    }

    [Fact]
    public void Parse_EmptyLimitsArray_FallsBackToLegacy()
    {
        var limits = ClaudeClient.Parse(Json.Parse("""
            { "limits": [], "seven_day": { "utilization": 20, "resets_at": null } }
            """));
        Assert.Equal("Week — all models", limits.Single().Title);
    }

    [Fact]
    public void Parse_NothingKnown_ReturnsEmpty() =>
        Assert.Empty(ClaudeClient.Parse(Json.Parse("{}")));

    [Fact]
    public async Task Fetch_SendsTokenAndBetaHeader_AndReturnsPlan()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(HttpStatusCode.OK, LimitsResponse);

        var section = await ClaudeClient.FetchAsync(dir.Write("creds.json", Credentials()), handler.Client(), Now);

        Assert.Equal("Claude", section.Name);
        Assert.Equal("max", section.Plan);
        Assert.Equal(3, section.Limits.Count);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-ant-oat-test", handler.Request.Headers.Authorization!.ToString());
        Assert.Equal("oauth-2025-04-20", handler.Header("anthropic-beta"));
    }

    [Fact]
    public async Task Fetch_MissingCredentials_Throws()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(HttpStatusCode.OK);
        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            ClaudeClient.FetchAsync(dir.File_("nope.json"), handler.Client(), Now));
        Assert.Contains("not found", ex.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Fetch_NoOauthSection_Throws()
    {
        using var dir = new TempDir();
        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            ClaudeClient.FetchAsync(dir.Write("c.json", """{ "mcpOAuth": {} }"""), new FakeHandler(HttpStatusCode.OK).Client(), Now));
        Assert.Contains("/login", ex.Message);
    }

    [Fact]
    public async Task Fetch_ExpiredToken_ThrowsWithoutCallingApi()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(HttpStatusCode.OK, LimitsResponse);
        var path = dir.Write("c.json", Credentials(Now.AddMinutes(-1).ToUnixTimeMilliseconds()));

        var ex = await Assert.ThrowsAsync<UsageException>(() => ClaudeClient.FetchAsync(path, handler.Client(), Now));
        Assert.Contains("expired", ex.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.TooManyRequests, "HTTP 429")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    public async Task Fetch_HttpError_Throws(HttpStatusCode status, string expected)
    {
        using var dir = new TempDir();
        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            ClaudeClient.FetchAsync(dir.Write("c.json", Credentials()), new FakeHandler(status).Client(), Now));
        Assert.Contains(expected, ex.Message);
    }
}
