namespace Muthur.Launch;

public static class HarnessDefaults
{
    /// <summary>
    /// Written to {MUTHUR_HOME}/harnesses.json on first start; the founder edits it from there.
    /// Order within a tier is preference order: the first candidate whose account is not limited gets the work.
    /// A candidate with <c>"enabled": false</c> stays in the file as a toggle and is never staffed; the local
    /// Ollama models ship that way because the hardware could not keep up with the organization.
    /// </summary>
    public const string CatalogJson = """
        {
          "tiers": {
            "mastermind": [
              { "harness": "claude", "model": "fable", "account": "claude-subscription" },
              { "harness": "codex", "model": "gpt-6-astra", "reasoningEffort": "medium", "account": "chatgpt-subscription" }
            ],
            "implementer": [
              { "harness": "claude", "model": "opus", "account": "claude-subscription" },
              { "harness": "codex", "model": "gpt-6-astra", "reasoningEffort": "medium", "account": "chatgpt-subscription" }
            ],
            "local-implementer": [],
            "utility": [
              { "harness": "codex-oss", "model": "gemma4:26b", "account": "local", "enabled": false },
              { "harness": "codex-oss", "model": "qwen3.8:27b", "account": "local", "enabled": false }
            ]
          }
        }

        """;
}
