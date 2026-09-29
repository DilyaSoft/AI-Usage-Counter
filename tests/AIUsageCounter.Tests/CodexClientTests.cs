using System.Net;

namespace AIUsageCounter.Tests;

public class CodexClientTests
{
    private const string Auth = """
    {
      "auth_mode": "chatgpt",
      "OPENAI_API_KEY": null,
      "tokens": { "id_token": "id", "access_token": "codex-token", "refresh_token": "r", "account_id": "acc-123" }
    }
    """;

    private const string WeeklyResponse = """
    {
      "plan_type": "pro",
      "rate_limit": {
        "allowed": true,
        "primary_window": { "used_percent": 40, "limit_window_seconds": 604800, "reset_after_seconds": 379530, "reset_at": 1791046917 },
        "secondary_window": null
      },
      "code_review_rate_limit": null,
      "additional_rate_limits": null
    }
    """;

    [Fact]
    public void Parse_WeeklyWindow()
    {
        var section = CodexClient.Parse(Json.Parse(WeeklyResponse));

        Assert.Equal("Codex", section.Name);
        Assert.Equal("pro", section.Plan);
        var l = Assert.Single(section.Limits);
        Assert.Equal("Week", l.Title);
        Assert.Equal(40, l.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791046917), l.ResetsAt);
    }

    [Fact]
    public void Parse_PrimarySecondaryCodeReviewAndAdditional()
    {
        var section = CodexClient.Parse(Json.Parse("""
            {
              "plan_type": "plus",
              "rate_limit": {
                "primary_window": { "used_percent": 12, "limit_window_seconds": 18000, "reset_at": 1791000000 },
                "secondary_window": { "used_percent": 55, "limit_window_seconds": 604800, "reset_at": 1791046917 }
              },
              "code_review_rate_limit": {
                "primary_window": { "used_percent": 5, "limit_window_seconds": 604800 }
              },
              "additional_rate_limits": [
                { "limit_name": "GPT-6 Astra", "rate_limit": { "primary_window": { "used_percent": 90, "limit_window_seconds": 86400 } } },
                { "rate_limit": { "primary_window": { "used_percent": 1, "limit_window_seconds": 7200 } } }
              ]
            }
            """));

        Assert.Equal(
            new[] { "Session (5h)", "Week", "Code review — Week", "GPT-6 Astra — Window 1d", "Extra limit — Window 2h" },
            section.Limits.Select(l => l.Title));
        Assert.Equal(new double[] { 12.0, 55, 5, 90, 1 }, section.Limits.Select(l => l.Percent));
        Assert.Null(section.Limits[2].ResetsAt);
    }

    [Fact]
    public void Parse_NoRateLimits_ReturnsEmptyAndNullPlan()
    {
        var section = CodexClient.Parse(Json.Parse("{}"));
        Assert.Empty(section.Limits);
        Assert.Null(section.Plan);
    }

    [Theory]
    [InlineData(18_000, "Session (5h)")]
    [InlineData(604_800, "Week")]
    [InlineData(86_400, "Window 1d")]
    [InlineData(172_800, "Window 2d")]
    [InlineData(3_600, "Window 1h")]
    [InlineData(5_400, "Window 1.5h")]
    [InlineData(0, "Limit")]
    public void WindowName_FormatsKnownAndCustomWindows(long seconds, string expected) =>
        Assert.Equal(expected, CodexClient.WindowName(seconds));

    [Fact]
    public async Task Fetch_SendsTokenAndAccountHeader()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(HttpStatusCode.OK, WeeklyResponse);

        var section = await CodexClient.FetchAsync(dir.Write("auth.json", Auth), handler.Client());

        Assert.Single(section.Limits);
        Assert.Equal("https://chatgpt.com/backend-api/wham/usage", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer codex-token", handler.Request.Headers.Authorization!.ToString());
        Assert.Equal("acc-123", handler.Header("ChatGPT-Account-Id"));
    }

    [Fact]
    public async Task Fetch_WithoutAccountId_OmitsHeader()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(HttpStatusCode.OK, WeeklyResponse);
        await CodexClient.FetchAsync(dir.Write("auth.json", """{ "tokens": { "access_token": "t" } }"""), handler.Client());
        Assert.Null(handler.Header("ChatGPT-Account-Id"));
    }

    [Fact]
    public async Task Fetch_ApiKeyModeWithoutTokens_Throws()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(HttpStatusCode.OK);
        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            CodexClient.FetchAsync(dir.Write("auth.json", """{ "OPENAI_API_KEY": "sk-x", "tokens": null }"""), handler.Client()));
        Assert.Contains("codex login", ex.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401: Codex token invalid")]
    [InlineData(HttpStatusCode.Forbidden, "403: Codex token invalid")]
    [InlineData(HttpStatusCode.BadGateway, "Codex HTTP 502")]
    public async Task Fetch_HttpError_Throws(HttpStatusCode status, string expected)
    {
        using var dir = new TempDir();
        var ex = await Assert.ThrowsAsync<UsageException>(() =>
            CodexClient.FetchAsync(dir.Write("auth.json", Auth), new FakeHandler(status).Client()));
        Assert.StartsWith(expected, ex.Message);
    }
}
