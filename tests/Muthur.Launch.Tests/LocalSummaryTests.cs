using System.Net;
using System.Text;
using System.Text.Json;
using Muthur.Launch;

namespace Muthur.Launch.Tests;

public sealed class LocalSummaryTests
{
    [Fact]
    public async Task A_large_log_is_bounded_and_usage_is_preserved_without_tools_or_streaming()
    {
        var handler = new Stub();
        using var client = new HttpClient(handler);
        var result = await LocalSummary.RunAsync(client, new Uri("http://127.0.0.1:11434"), "test:local", new string('x', 30000));
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.True(sent.RootElement.GetProperty("prompt").GetString()!.Length < LocalSummary.MaxInputCharacters);
        Assert.False(sent.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(sent.RootElement.TryGetProperty("tools", out _));
        Assert.Equal(512, sent.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.True(result.Truncated);
        Assert.Equal(123, result.InputTokens);
        Assert.Equal(45, result.OutputTokens);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Unavailable_local_service_is_not_retried_or_sent_to_another_provider()
    {
        var handler = new Stub { Status = HttpStatusCode.ServiceUnavailable };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => LocalSummary.RunAsync(client, new Uri("http://localhost:11434"), "test", "evidence"));
        Assert.Equal(1, handler.Calls);
        await Assert.ThrowsAsync<ArgumentException>(() => LocalSummary.RunAsync(client, new Uri("https://example.com"), "test", "evidence"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Local_jobs_share_an_exclusive_lease_and_release_it_on_disposal()
    {
        var home = Path.Combine(Path.GetTempPath(), "muthur-local-lease-" + Guid.NewGuid().ToString("n"));
        try
        {
            using (LocalInferenceLease.Acquire(home)) Assert.Throws<IOException>(() => LocalInferenceLease.Acquire(home));
            using var next = LocalInferenceLease.Acquire(home);
        }
        finally { Directory.Delete(home, true); }
    }

    private sealed class Stub : HttpMessageHandler
    {
        public int Calls;
        public string? Body;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(Status) { Content = new StringContent("{\"done\":true,\"response\":\"Failure in TestA at a.cs:3\",\"prompt_eval_count\":123,\"eval_count\":45}", Encoding.UTF8, "application/json") };
        }
    }
}
