using System.Runtime.InteropServices;
using System.Text;

namespace AIUsageCounter;

/// <summary>
/// Renews the Codex ChatGPT login by starting codex.exe. Codex stores no separate expiry;
/// the access token is a JWT and its <c>exp</c> claim is the signal that a new token is on disk.
/// </summary>
internal static class CodexCliRefresh
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static Task<bool> TryRenewAsync(string authPath, CancellationToken ct = default) =>
        CliRefresh.RenewAsync(() => TryReadExpiry(authPath), FindLaunch, Gate, ct);

    internal static CliRefresh.Launch? FindLaunch()
    {
        var candidates = new List<string>();
        foreach (string cmd in CliRefresh.PathExes("codex.cmd"))
        {
            if (!File.Exists(cmd)) continue;
            string? dir = Path.GetDirectoryName(cmd);
            if (dir is null) continue;
            candidates.Add(NativePath(dir));
        }
        candidates.AddRange(CliRefresh.PathExes("codex.exe"));
        return CliRefresh.FirstLaunch(candidates);
    }

    internal static string NativePath(string shimDirectory) =>
        Path.Combine(
            shimDirectory, "node_modules", "@openai", "codex", "node_modules", "@openai", PackageName(),
            "vendor", Triple(), "bin", "codex.exe");

    internal static DateTimeOffset? TryReadExpiry(string authPath)
    {
        try
        {
            using var auth = System.Text.Json.JsonDocument.Parse(File.ReadAllText(authPath));
            if (!auth.RootElement.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;
            if (!tokens.TryGetProperty("access_token", out var tokenEl) || tokenEl.ValueKind != System.Text.Json.JsonValueKind.String)
                return null;
            return TryReadJwtExpiry(tokenEl.GetString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    internal static DateTimeOffset? TryReadJwtExpiry(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        string[] parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            using var payload = System.Text.Json.JsonDocument.Parse(Base64UrlDecode(parts[1]));
            if (payload.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out long seconds))
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or ArgumentException)
        {
        }
        return null;
    }

    internal static string JwtWithExpiry(DateTimeOffset exp)
    {
        static string B64(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        string payload = B64(FormattableString.Invariant($"{{\"exp\":{exp.ToUnixTimeSeconds()}}}"));
        return $"{B64("{\"alg\":\"none\"}")}.{payload}.x";
    }

    private static string PackageName() =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "codex-win32-arm64" : "codex-win32-x64";

    private static string Triple() =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? "aarch64-pc-windows-msvc"
            : "x86_64-pc-windows-msvc";

    private static byte[] Base64UrlDecode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => throw new FormatException("Invalid JWT payload"),
        };
        return Convert.FromBase64String(padded);
    }
}
