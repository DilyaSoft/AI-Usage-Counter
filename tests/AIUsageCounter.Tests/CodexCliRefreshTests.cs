using System.Net;
using System.Runtime.InteropServices;

namespace AIUsageCounter.Tests;

public class CodexCliRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 7, 30, 0, TimeSpan.Zero);

    [Fact]
    public void TryReadExpiry_ReadsJwtExpAndIgnoresLastRefresh()
    {
        using var dir = new TempDir();
        string token = CodexCliRefresh.JwtWithExpiry(Now.AddHours(2));
        string path = dir.Write("auth.json", $$"""
        {
          "last_refresh": "2020-01-01T00:00:00Z",
          "tokens": { "access_token": "{{token}}", "refresh_token": "r" }
        }
        """);

        Assert.Equal(Now.AddHours(2), CodexCliRefresh.TryReadExpiry(path));
    }

    [Fact]
    public void TryReadExpiry_NonJwtOrBroken_ReturnsNull()
    {
        using var dir = new TempDir();
        Assert.Null(CodexCliRefresh.TryReadJwtExpiry("codex-token"));
        Assert.Null(CodexCliRefresh.TryReadJwtExpiry("a.%%% .c"));
        Assert.Null(CodexCliRefresh.TryReadExpiry(dir.Write("auth.json", """{ "tokens": { "access_token": "codex-token" } }""")));
        Assert.Null(CodexCliRefresh.TryReadExpiry(dir.File_("missing.json")));
    }

    [Fact]
    public void NativePath_PointsAtTheVendoredBinary()
    {
        bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        string path = CodexCliRefresh.NativePath(@"D:\npm");
        Assert.EndsWith(
            Path.Combine("node_modules", "@openai", "codex", "node_modules", "@openai",
                arm ? "codex-win32-arm64" : "codex-win32-x64", "vendor",
                arm ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc", "bin", "codex.exe"),
            path);
    }

    [Fact]
    public async Task Fetch_ExpiredJwt_ThrowsWithoutCallingApi()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(HttpStatusCode.OK, """{ "plan_type": "pro" }""");
        string token = CodexCliRefresh.JwtWithExpiry(Now.AddMinutes(-5));
        string auth = dir.Write("auth.json", $$"""{ "tokens": { "access_token": "{{token}}", "account_id": "a" } }""");

        var ex = await Assert.ThrowsAsync<UsageException>(() => CodexClient.FetchAsync(auth, handler.Client(), Now));

        Assert.Contains("expired", ex.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Fetch_FutureJwt_IsSent()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler(HttpStatusCode.OK, """{ "plan_type": "pro", "rate_limit": {} }""");
        string token = CodexCliRefresh.JwtWithExpiry(Now.AddHours(1));
        string auth = dir.Write("auth.json", $$"""{ "tokens": { "access_token": "{{token}}" } }""");

        await CodexClient.FetchAsync(auth, handler.Client(), Now);

        Assert.Equal($"Bearer {token}", handler.Request!.Headers.Authorization!.ToString());
    }
}
