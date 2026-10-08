namespace AIUsageCounter.Tests;

public class ClaudeCliRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 7, 30, 0, TimeSpan.Zero);

    [Fact]
    public void TryReadExpiry_UsesClaudeOauthAndIgnoresMcp()
    {
        using var dir = new TempDir();
        long claudeMs = Now.ToUnixTimeMilliseconds();
        string path = dir.Write("creds.json", $$"""
        {
          "mcpOAuth": { "notion": { "expiresAt": {{Now.AddDays(30).ToUnixTimeMilliseconds()}} } },
          "claudeAiOauth": { "accessToken": "t", "expiresAt": {{claudeMs}} }
        }
        """);

        Assert.Equal(Now, ClaudeCliRefresh.TryReadExpiry(path));
    }

    [Fact]
    public void TryReadExpiry_MissingOrBroken_ReturnsNull()
    {
        using var dir = new TempDir();
        Assert.Null(ClaudeCliRefresh.TryReadExpiry(dir.File_("missing.json")));
        Assert.Null(ClaudeCliRefresh.TryReadExpiry(dir.Write("bad.json", "{")));
        Assert.Null(ClaudeCliRefresh.TryReadExpiry(dir.Write("mcp.json", """{ "mcpOAuth": { "expiresAt": 1 } }""")));
    }

    [Fact]
    public void FindLaunch_PrefersTheExeNextToTheShim()
    {
        using var dir = new TempDir();
        string exe = Path.Combine(dir.Path, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "x");
        string other = dir.Write("other.exe", "x");

        Assert.Equal(exe, CliRefresh.FirstLaunch([exe, other])!.FileName);
        Assert.Null(CliRefresh.FirstLaunch(["missing.exe"]));
    }
}
