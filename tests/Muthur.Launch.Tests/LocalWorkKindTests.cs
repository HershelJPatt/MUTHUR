using System.Text;
using System.Text.Json;

namespace Muthur.Launch.Tests;

/// <summary>
/// A work-kind guess is the summarizer's one bounded call with a different instruction and a tiny output
/// budget, and its answer is a label or nothing: the model never gets to invent a fifth kind.
/// </summary>
public sealed class LocalWorkKindTests
{
    [Theory]
    [InlineData("mechanical", "mechanical")]
    [InlineData("  Complex-Debugging.\n", "complex-debugging")]
    [InlineData("\"ui-interaction\"", "ui-interaction")]
    [InlineData("general", "general")]
    [InlineData("This looks mechanical to me", "unknown")]
    [InlineData("refactor", "unknown")]
    [InlineData("", "unknown")]
    public void Only_one_of_the_four_labels_is_an_answer(string said, string expected) => Assert.Equal(expected, LocalWorkKind.Parse(said));

    [Fact]
    public async Task The_call_is_bounded_carries_the_fixed_instruction_and_keeps_the_raw_answer()
    {
        var handler = new Stub("Mechanical");
        using var client = new HttpClient(handler);
        var result = await LocalWorkKind.RunAsync(client, new Uri("http://127.0.0.1:11434"), "test:local", "Rename Foo to Bar everywhere");
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal(LocalWorkKind.SystemPrompt, sent.RootElement.GetProperty("system").GetString());
        Assert.Equal(LocalWorkKind.MaxOutputTokens, sent.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.False(sent.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(sent.RootElement.GetProperty("think").GetBoolean());
        Assert.Equal("mechanical", result.WorkKind);
        Assert.Equal("Mechanical", result.Raw);
        Assert.Equal(7, result.InputTokens);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class Stub(string answer) : HttpMessageHandler
    {
        public int Calls;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"done\":true,\"response\":\"{answer}\",\"prompt_eval_count\":7,\"eval_count\":1}}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
