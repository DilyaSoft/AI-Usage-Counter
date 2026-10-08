using System.Diagnostics;

namespace AIUsageCounter;

/// <summary>
/// Starts a CLI and stops that process only after its login file shows a new expiry.
/// The CLI rotates the refresh token itself. Killing before the new expiry is on disk
/// can drop a refresh the server has already accepted and sign the CLI out.
/// </summary>
internal static class CliRefresh
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultPoll = TimeSpan.FromMilliseconds(200);

    private static readonly List<LiveChildProcess> Live = [];
    private static int _exitHooked;

    internal sealed record Launch(string FileName, string[]? Arguments = null);

    public static async Task<bool> RenewAsync(
        Func<DateTimeOffset?> readExpiry, Func<Launch?> locate, SemaphoreSlim gate, CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(DefaultTimeout, ct)) return false;
        try
        {
            return await TryRenewAsync(readExpiry, locate(), Start, DateTimeOffset.UtcNow, DefaultTimeout, DefaultPoll, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    internal static async Task<bool> TryRenewAsync(
        Func<DateTimeOffset?> readExpiry,
        Launch? launch,
        Func<Launch, IChildProcess> start,
        DateTimeOffset now,
        TimeSpan timeout,
        TimeSpan poll,
        CancellationToken ct = default)
    {
        if (launch is null || string.IsNullOrWhiteSpace(launch.FileName)) return false;

        DateTimeOffset? before = readExpiry();
        IChildProcess proc;
        try
        {
            proc = start(launch);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }

        using (proc)
        {
            bool renewed = false;
            try
            {
                renewed = await WaitForNewExpiry(readExpiry, before, now, proc, timeout, poll, ct);
            }
            finally
            {
                proc.Kill();
            }

            if (!renewed) return false;
            // The file has to still parse after the process is gone. A kill during a later rewrite
            // must not count as success.
            var settle = Stopwatch.StartNew();
            while (settle.Elapsed < TimeSpan.FromSeconds(2))
            {
                ct.ThrowIfCancellationRequested();
                if (IsNewer(readExpiry(), before, now)) return true;
                await Task.Delay(poll, ct);
            }
            return false;
        }
    }

    internal static Launch? FirstLaunch(IEnumerable<string> candidates)
    {
        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                if (File.Exists(candidate)) return new Launch(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
        }
        return null;
    }

    internal static IEnumerable<string> PathExes(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) yield break;
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = dir.Trim().Trim('"');
            if (trimmed.Length == 0) continue;
            yield return Path.Combine(trimmed, fileName);
        }
    }

    private static async Task<bool> WaitForNewExpiry(
        Func<DateTimeOffset?> readExpiry, DateTimeOffset? before, DateTimeOffset now, IChildProcess proc,
        TimeSpan timeout, TimeSpan poll, CancellationToken ct)
    {
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsStable(readExpiry, before, now, poll, ct)) return true;
            if (proc.HasExited) return false;
            await Task.Delay(poll, ct);
        }
        return false;
    }

    /// <summary>Two identical reads, so a half-written login file is not treated as the new token.</summary>
    private static async Task<bool> IsStable(
        Func<DateTimeOffset?> readExpiry, DateTimeOffset? before, DateTimeOffset now, TimeSpan poll, CancellationToken ct)
    {
        DateTimeOffset? first = readExpiry();
        if (!IsNewer(first, before, now)) return false;
        await Task.Delay(poll, ct);
        return readExpiry() == first;
    }

    private static bool IsNewer(DateTimeOffset? expiry, DateTimeOffset? before, DateTimeOffset now) =>
        expiry is DateTimeOffset exp && exp > now && (before is null || exp > before);

    private static IChildProcess Start(Launch launch)
    {
        HookExit();
        var psi = new ProcessStartInfo(launch.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (launch.Arguments is not null)
            foreach (string arg in launch.Arguments)
                psi.ArgumentList.Add(arg);

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {launch.FileName}");
        var child = new LiveChildProcess(process);
        lock (Live) Live.Add(child);
        return child;
    }

    private static void HookExit()
    {
        if (Interlocked.Exchange(ref _exitHooked, 1) == 1) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            LiveChildProcess[] snapshot;
            lock (Live) snapshot = [.. Live];
            foreach (var child in snapshot) child.Kill();
        };
    }

    private sealed class LiveChildProcess(Process process) : IChildProcess
    {
        public bool HasExited => process.HasExited;

        public void Kill()
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            lock (Live) Live.Remove(this);
        }

        public void Dispose() => process.Dispose();
    }
}

internal interface IChildProcess : IDisposable
{
    bool HasExited { get; }
    void Kill();
}
