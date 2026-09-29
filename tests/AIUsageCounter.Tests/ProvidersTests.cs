namespace AIUsageCounter.Tests;

public class ProvidersTests
{
    private static UsageProvider Fake(string name, bool detected) =>
        new(name, Color.Gray, "https://example.com", () => detected, () => Task.FromResult(new UsageSection(name, null, [])));

    [Fact]
    public void Active_OnlyDetectedAndNotHidden()
    {
        var all = new[] { Fake("Claude", true), Fake("Codex", false), Fake("Grok", true) };

        Assert.Equal(new[] { "Claude", "Grok" }, Providers.Active(all, []).Select(p => p.Name));
        Assert.Equal(new[] { "Claude" }, Providers.Active(all, ["grok"]).Select(p => p.Name));
        Assert.Empty(Providers.Active(all, ["Claude", "Grok"]));
    }

    [Fact]
    public void Active_KeepsDeclaredOrder()
    {
        var all = new[] { Fake("B", true), Fake("A", true) };
        Assert.Equal(new[] { "B", "A" }, Providers.Active(all, []).Select(p => p.Name));
    }

    [Fact]
    public void All_HasUniqueNamesAndHttpsUsagePages()
    {
        Assert.Equal(Providers.All.Count, Providers.All.Select(p => p.Name).Distinct().Count());
        Assert.All(Providers.All, p => Assert.StartsWith("https://", p.UsagePageUrl));
    }

    [Fact]
    public void CliHome_DefaultsToUserProfile() =>
        Assert.Equal(Path.Combine(@"C:\Users\me", ".codex"),
            CliHome.Resolve("CODEX_HOME", ".codex", _ => null, @"C:\Users\me"));

    [Fact]
    public void CliHome_EnvVarOverrides() =>
        Assert.Equal(@"D:\cli\codex",
            CliHome.Resolve("CODEX_HOME", ".codex", v => v == "CODEX_HOME" ? @" D:\cli\codex " : null, @"C:\Users\me"));

    [Fact]
    public void CliHome_BlankEnvVarIsIgnored() =>
        Assert.Equal(Path.Combine(@"C:\Users\me", ".grok"),
            CliHome.Resolve("GROK_HOME", ".grok", _ => "   ", @"C:\Users\me"));
}
