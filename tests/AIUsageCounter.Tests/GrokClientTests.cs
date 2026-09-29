using System.Net;
using System.Text.Json;

namespace AIUsageCounter.Tests;

public class GrokClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 7, 30, 0, TimeSpan.Zero);

    private const string BillingResponse = """
    {
      "config": {
        "currentPeriod": { "type": "USAGE_PERIOD_TYPE_WEEKLY", "start": "2026-09-26T16:52:02.85+00:00", "end": "2026-10-03T16:52:02.85+00:00" },
        "creditUsagePercent": 21.0,
        "productUsage": [ { "product": "GrokBuild", "usagePercent": 21.0 } ],
        "prepaidBalance": { "val": 242 }
      }
    }
    """;

    private static string Auth(DateTimeOffset expiresAt) => $$"""
    {
      "https://auth.x.ai::client-id": {
        "key": "grok-token",
        "auth_mode": "oidc",
        "refresh_token": "r",
        "expires_at": "{{expiresAt:O}}"
      }
    }
    """;

    private static string SettingsCache(string tier) =>
        JsonSerializer.Serialize(new
        {
            payload = JsonSerializer.Serialize(new { settings = new { ui = new { subscription_tier = (string?)null, subscription_tier_display = tier } } }),
        });

    [Fact]
    public void Parse_WeeklyPeriod_SingleProductIsNotRepeated()
    {
        var l = Assert.Single(GrokClient.Parse(Json.Parse(BillingResponse)));
        Assert.Equal("Week", l.Title);
        Assert.Equal(21, l.Percent);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 16, 52, 2, 850, TimeSpan.Zero), l.ResetsAt);
    }

    [Fact]
    public void Parse_MultipleProducts_AddsBreakdown()
    {
        var limits = GrokClient.Parse(Json.Parse("""
            {
              "config": {
                "currentPeriod": { "type": "USAGE_PERIOD_TYPE_MONTHLY", "end": "2026-10-31T00:00:00Z" },
                "creditUsagePercent": 30,
                "productUsage": [ { "product": "GrokBuild", "usagePercent": 20 }, { "product": "Imagine", "usagePercent": 10 } ]
              }
            }
            """));

        Assert.Equal(new[] { "Month", "Month — GrokBuild", "Month — Imagine" }, limits.Select(l => l.Title));
        Assert.Equal(new double[] { 30.0, 20, 10 }, limits.Select(l => l.Percent));
        Assert.All(limits, l => Assert.NotNull(l.ResetsAt));
    }

    [Theory]
    [InlineData("USAGE_PERIOD_TYPE_DAILY", "Day")]
    [InlineData("USAGE_PERIOD_TYPE_WEEKLY", "Week")]
    [InlineData("USAGE_PERIOD_TYPE_MONTHLY", "Month")]
    [InlineData("USAGE_PERIOD_TYPE_SOMETHING_NEW", "Period")]
    public void Parse_PeriodTypes(string type, string expected)
    {
        var limits = GrokClient.Parse(Json.Parse($$"""{ "config": { "currentPeriod": { "type": "{{type}}" }, "creditUsagePercent": 1 } }"""));
        Assert.Equal(expected, limits.Single().Title);
    }

    [Fact]
    public void Parse_WithoutConfigWrapperOrPeriod()
    {
        var l = Assert.Single(GrokClient.Parse(Json.Parse("""{ "creditUsagePercent": 7.5 }""")));
        Assert.Equal("Period", l.Title);
        Assert.Equal(7.5, l.Percent);
        Assert.Null(l.ResetsAt);
    }

    [Fact]
    public void ReadPlan_FindsNestedTierDisplay()
    {
        using var dir = new TempDir();
        Assert.Equal("SuperGrok Plus", GrokClient.ReadPlan(dir.Write("s.json", SettingsCache("SuperGrok Plus"))));
    }

    [Fact]
    public void ReadPlan_MissingOrBrokenFile_ReturnsNull()
    {
        using var dir = new TempDir();
        Assert.Null(GrokClient.ReadPlan(dir.File_("missing.json")));
        Assert.Null(GrokClient.ReadPlan(dir.Write("bad.json", "not json")));
        Assert.Null(GrokClient.ReadPlan(dir.Write("nopayload.json", "{}")));
    }

    [Fact]
    public async Task Fetch_SendsTokenAndCombinesPlan()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(HttpStatusCode.OK, BillingResponse);

        var section = await GrokClient.FetchAsync(
            dir.Write("auth.json", Auth(Now.AddHours(2))), dir.Write("s.json", SettingsCache("SuperGrok Plus")),
            handler.Client(), Now);

        Assert.Equal("Grok", section.Name);
        Assert.Equal("SuperGrok Plus", section.Plan);
        Assert.Single(section.Limits);
        Assert.Equal("https://cli-chat-proxy.grok.com/v1/billing?format=credits", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer grok-token", handler.Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Fetch_SkipsEntriesWithoutKey()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(HttpStatusCode.OK, BillingResponse);
        var auth = dir.Write("auth.json", $$"""
            { "version": 2, "other": { "foo": 1 }, "https://auth.x.ai::c": { "key": "second", "expires_at": "{{Now.AddHours(1):O}}" } }
            """);

        await GrokClient.FetchAsync(auth, dir.File_("none.json"), handler.Client(), Now);
        Assert.Equal("Bearer second", handler.Request!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Fetch_ExpiredToken_ThrowsWithoutCallingApi()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(HttpStatusCode.OK, BillingResponse);
        var ex = await Assert.ThrowsAsync<UsageException>(() => GrokClient.FetchAsync(
            dir.Write("auth.json", Auth(Now.AddMinutes(-5))), dir.File_("none.json"), handler.Client(), Now));
        Assert.Contains("expired", ex.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Fetch_NotSignedIn_Throws()
    {
        using var dir = new TempDir();
        var ex = await Assert.ThrowsAsync<UsageException>(() => GrokClient.FetchAsync(
            dir.Write("auth.json", "{}"), dir.File_("none.json"), new FakeHandler(HttpStatusCode.OK).Client(), Now));
        Assert.Contains("grok login", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401: Grok token invalid")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Grok HTTP 503")]
    public async Task Fetch_HttpError_Throws(HttpStatusCode status, string expected)
    {
        using var dir = new TempDir();
        var ex = await Assert.ThrowsAsync<UsageException>(() => GrokClient.FetchAsync(
            dir.Write("auth.json", Auth(Now.AddHours(1))), dir.File_("none.json"), new FakeHandler(status).Client(), Now));
        Assert.StartsWith(expected, ex.Message);
    }
}
