namespace Muthur.Launch;

/// <summary>Provider choices and initial routing for organization oversight.</summary>
public static class OverseerDefaults
{
    public const string Harness = "codex";
    public const string Model = "gpt-6-astra";
    public static IReadOnlyList<(string Key, string Label)> Providers { get; } =
        [("codex", "OpenAI / Codex"), ("claude", "Anthropic / Claude")];
    public static bool Supports(string harness) => Providers.Any(x => x.Key == harness);
}
