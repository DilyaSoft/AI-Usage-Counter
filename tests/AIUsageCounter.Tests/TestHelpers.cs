using System.Net;
using System.Text;
using System.Text.Json;

namespace AIUsageCounter.Tests;

/// <summary>HttpMessageHandler that returns a canned response and remembers the request it got.</summary>
public sealed class FakeHandler(HttpStatusCode status, string body = "{}") : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Request = request;
        Calls++;
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }

    public HttpClient Client() => new(this);

    public string? Header(string name) =>
        Request!.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : null;
}

/// <summary>Temporary directory with helpers for writing fixture files; deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AIUsageCounterTests_" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Write(string name, string content)
    {
        string p = System.IO.Path.Combine(Path, name);
        File.WriteAllText(p, content);
        return p;
    }

    public string File_(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}

public static class Json
{
    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
