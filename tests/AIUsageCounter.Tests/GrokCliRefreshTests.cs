namespace AIUsageCounter.Tests;

public class GrokCliRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 7, 30, 0, TimeSpan.Zero);

    private static string Auth(DateTimeOffset expiresAt) => $$"""
    {
      "https://auth.x.ai::client-id": {
        "key": "grok-token",
        "refresh_token": "r",
        "expires_at": "{{expiresAt:O}}"
      }
    }
    """;

    [Fact]
    public async Task Renew_KillsProcessOnceExpiryIsStableAndInTheFuture()
    {
        using var dir = new TempDir();
        string auth = dir.Write("auth.json", Auth(Now.AddMinutes(-5)));
        var proc = new FakeProc();

        var result = await GrokCliRefresh.TryRenewAsync(
            auth, "grok.exe", _ =>
            {
                Task.Run(async () =>
                {
                    await Task.Delay(30);
                    File.WriteAllText(auth, Auth(Now.AddHours(6)));
                });
                return proc;
            },
            Now, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10));

        Assert.True(result);
        Assert.Equal(1, proc.Kills);
        Assert.True(GrokCliRefresh.TryReadExpiry(auth) > Now);
    }

    [Fact]
    public async Task Renew_ExpiryThatMovesButStaysInThePast_DoesNotCount()
    {
        using var dir = new TempDir();
        string auth = dir.Write("auth.json", Auth(Now.AddMinutes(-10)));
        var proc = new FakeProc();

        var result = await GrokCliRefresh.TryRenewAsync(
            auth, "grok.exe", _ =>
            {
                File.WriteAllText(auth, Auth(Now.AddMinutes(-1)));
                return proc;
            },
            Now, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(10));

        Assert.False(result);
        Assert.Equal(1, proc.Kills);
    }

    [Fact]
    public async Task Renew_UnchangedFile_TimesOutAndKills()
    {
        using var dir = new TempDir();
        string auth = dir.Write("auth.json", Auth(Now.AddMinutes(-5)));
        var proc = new FakeProc();

        var result = await GrokCliRefresh.TryRenewAsync(
            auth, "grok.exe", _ => proc, Now, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(10));

        Assert.False(result);
        Assert.Equal(1, proc.Kills);
    }

    [Fact]
    public async Task Renew_ProcessExitsBeforeTheFileChanges_ReturnsFalse()
    {
        using var dir = new TempDir();
        string auth = dir.Write("auth.json", Auth(Now.AddMinutes(-5)));
        var proc = new FakeProc { HasExited = true };

        var result = await GrokCliRefresh.TryRenewAsync(
            auth, "grok.exe", _ => proc, Now, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10));

        Assert.False(result);
        Assert.Equal(1, proc.Kills);
    }

    [Fact]
    public async Task Renew_MissingExecutable_DoesNotStart()
    {
        using var dir = new TempDir();
        string auth = dir.Write("auth.json", Auth(Now.AddMinutes(-5)));
        bool started = false;

        var result = await GrokCliRefresh.TryRenewAsync(
            auth, null, _ => { started = true; return new FakeProc(); },
            Now, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));

        Assert.False(result);
        Assert.False(started);
    }

    [Fact]
    public async Task Renew_StartFailure_ReturnsFalse()
    {
        using var dir = new TempDir();
        string auth = dir.Write("auth.json", Auth(Now.AddMinutes(-5)));

        var result = await GrokCliRefresh.TryRenewAsync(
            auth, "grok.exe", _ => throw new InvalidOperationException("nope"),
            Now, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));

        Assert.False(result);
    }

    [Fact]
    public void FindGrokExe_UsesTheFirstCandidateThatExists()
    {
        using var dir = new TempDir();
        string exe = dir.Write("grok.exe", "not a real binary");
        Assert.Equal(exe, GrokCliRefresh.FindGrokExe(["missing.exe", exe, dir.Write("other.exe", "x")]));
        Assert.Null(GrokCliRefresh.FindGrokExe(["missing.exe"]));
    }

    [Fact]
    public void TryReadExpiry_IgnoresLockedOrBrokenFiles()
    {
        using var dir = new TempDir();
        Assert.Null(GrokCliRefresh.TryReadExpiry(dir.File_("missing.json")));
        Assert.Null(GrokCliRefresh.TryReadExpiry(dir.Write("bad.json", "{")));
        Assert.Equal(Now, GrokCliRefresh.TryReadExpiry(dir.Write("auth.json", Auth(Now))));
    }

    private sealed class FakeProc : IChildProcess
    {
        public bool HasExited { get; set; }
        public int Kills { get; private set; }
        public void Kill() => Kills++;
        public void Dispose() { }
    }
}
