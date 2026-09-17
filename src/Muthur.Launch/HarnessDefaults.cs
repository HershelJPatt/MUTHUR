namespace Muthur.Launch;

public static class HarnessDefaults
{
    /// <summary>
    /// Written to {MUTHUR_HOME}/harnesses.json on first start; the founder edits it from there.
    /// Order within a tier is preference order: the first candidate whose account is not limited gets the work.
    /// </summary>
    public const string CatalogJson = """
        {
          "tiers": {
            "mastermind": [
              { "harness": "claude", "model": "fable", "account": "claude-subscription" },
              { "harness": "codex", "model": "", "account": "chatgpt-subscription" }
            ],
            "implementer": [
              { "harness": "claude", "model": "opus", "account": "claude-subscription" },
              { "harness": "codex", "model": "", "account": "chatgpt-subscription" }
            ],
            "utility": [
              { "harness": "codex-oss", "model": "gpt-oss:20b", "account": "local" },
              { "harness": "claude", "model": "haiku", "account": "claude-subscription" }
            ]
          }
        }

        """;
}
