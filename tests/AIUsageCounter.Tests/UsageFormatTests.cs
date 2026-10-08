namespace AIUsageCounter.Tests;

public class UsageFormatTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 10, 30, 0);

    [Theory]
    [InlineData(0, Severity.Normal)]
    [InlineData(69.9, Severity.Normal)]
    [InlineData(70, Severity.Warning)]
    [InlineData(89.9, Severity.Warning)]
    [InlineData(90, Severity.Critical)]
    [InlineData(100, Severity.Critical)]
    [InlineData(150, Severity.Critical)]
    [InlineData(-5, Severity.Normal)]
    public void SeverityOf_UsesThresholds(double percent, Severity expected) =>
        Assert.Equal(expected, UsageFormat.SeverityOf(percent));

    [Theory]
    [InlineData(54, "54% · 46% left")]
    [InlineData(0, "0% · 100% left")]
    [InlineData(100, "100% · 0% left")]
    [InlineData(120, "100% · 0% left")]
    [InlineData(-3, "0% · 100% left")]
    [InlineData(33.4, "33% · 67% left")]
    public void PercentText_ClampsAndRounds(double percent, string expected) =>
        Assert.Equal(expected, UsageFormat.PercentText(percent));

    [Fact]
    public void FormatReset_SameDay_ShowsTimeAndMinutes() =>
        Assert.Equal("11:20 (in 50m)", UsageFormat.FormatReset(Now.AddMinutes(50), Now));

    [Fact]
    public void FormatReset_SameDay_ShowsHoursAndMinutes() =>
        Assert.Equal("13:45 (in 3h 15m)", UsageFormat.FormatReset(new DateTime(2026, 9, 29, 13, 45, 0), Now));

    [Fact]
    public void FormatReset_OtherDay_ShowsWeekdayDateAndDays() =>
        Assert.Equal("Sun 04.10 16:00 (in 5d 5h)",
            UsageFormat.FormatReset(new DateTime(2026, 10, 4, 16, 0, 0), Now));

    [Fact]
    public void FormatReset_TomorrowWithinADay_ShowsDateButHours() =>
        Assert.Equal("Wed 30.09 08:00 (in 21h 30m)",
            UsageFormat.FormatReset(new DateTime(2026, 9, 30, 8, 0, 0), Now));

    [Fact]
    public void FormatReset_LessThanAMinute_RoundsUpToOneMinute() =>
        Assert.Equal("10:30 (in 1m)", UsageFormat.FormatReset(Now.AddSeconds(20), Now));

    [Fact]
    public void FormatReset_InThePast_ShowsOnlyTime() =>
        Assert.Equal("10:00", UsageFormat.FormatReset(Now.AddMinutes(-30), Now));

    [Fact]
    public async Task FetchSafe_Success_ReturnsResult()
    {
        var ok = new UsageSection("Claude", "max", [new("Week", 10, null)]);
        var result = await UsageFormat.FetchSafeAsync("Claude", () => Task.FromResult(ok), previous: null);
        Assert.Same(ok, result);
    }

    [Fact]
    public async Task FetchSafe_Failure_KeepsPreviousLimitsAndAddsError()
    {
        var previous = new UsageSection("Codex", "pro", [new("Week", 40, null)]);
        var result = await UsageFormat.FetchSafeAsync("Codex",
            () => throw new UsageException("token expired"), previous);

        Assert.Equal("token expired", result.Error);
        Assert.Equal("pro", result.Plan);
        Assert.Single(result.Limits);
        Assert.Equal(40, result.Limits[0].Percent);
    }

    [Fact]
    public async Task FetchSafe_FailureWithoutPrevious_ReturnsEmptySection()
    {
        var result = await UsageFormat.FetchSafeAsync("Grok", () => throw new InvalidOperationException("boom"), null);
        Assert.Equal("Grok", result.Name);
        Assert.Empty(result.Limits);
        Assert.Equal("boom", result.Error);
    }

    [Fact]
    public async Task FetchSafe_MapsNetworkAndTimeoutErrors()
    {
        var net = await UsageFormat.FetchSafeAsync("A", () => throw new HttpRequestException("no route"), null);
        Assert.Equal("Network: no route", net.Error);

        var timeout = await UsageFormat.FetchSafeAsync("A", () => throw new TaskCanceledException(), null);
        Assert.Equal("Request timed out", timeout.Error);
    }

    [Fact]
    public void ForDisplay_HidesClaudeFiveHourSessionUnlessEnabled()
    {
        var claude = new UsageSection("Claude", "max",
            [new("Session (5h)", 10, null), new("Week — all models", 20, null)]);
        var codex = new UsageSection("Codex", "plus",
            [new("Session (5h)", 9, null), new("Week", 1, null)]);

        Assert.Equal(["Week — all models"], UsageFormat.ForDisplay(claude, false).Limits.Select(l => l.Title));
        Assert.Equal(2, UsageFormat.ForDisplay(claude, true).Limits.Count);
        Assert.Equal(2, UsageFormat.ForDisplay(codex, false).Limits.Count);
    }

    [Fact]
    public async Task FetchSafe_SuccessAfterFailure_ClearsError()
    {
        var failed = new UsageSection("Claude", null, [], "old error");
        var fresh = new UsageSection("Claude", "max", []);
        var result = await UsageFormat.FetchSafeAsync("Claude", () => Task.FromResult(fresh), failed);
        Assert.Null(result.Error);
    }
}
