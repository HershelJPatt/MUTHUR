using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Muthur.Launch;

/// <summary>
/// The deny list for a harness with no sandbox: a pi extension whose <c>tool_call</c> handler refuses a shell command
/// (the <c>bash</c> tool, or the optional <c>powershell</c> one) matching any denied glob, and a write or edit under
/// <c>.git/</c>. A glob is matched against every command in a
/// chain — the start of the line and whatever follows <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c> or a newline —
/// so <c>echo x &amp;&amp; git push</c> is refused like <c>git push</c>. It is a gate against a model that forgets the
/// rules, not a boundary against one that sets out to evade them: <c>bash -c "git push"</c> passes.
/// </summary>
public static partial class PiGuard
{
    /// <summary>The words every refusal starts with; the capability probe looks for them in the event stream.</summary>
    public const string BlockMarker = "muthur-guard: blocked";

    public const string FileName = "pi-guard.ts";

    /// <summary>The extension's rule in C#, for callers that must know in advance whether a command will be refused.</summary>
    public static bool Denies(IReadOnlyList<string> denied, string command) =>
        Segments().Split(command).Select(s => s.Trim()).Where(s => s.Length > 0).Any(segment =>
            denied.Any(glob => glob.Trim().Length > 0 && Regex.IsMatch(segment,
                "^" + string.Join(".*", glob.Trim().Split('*').Select(Regex.Escape)) + "$",
                RegexOptions.Singleline)));

    [GeneratedRegex(@"&&|\|\||;|\||\r?\n")]
    private static partial Regex Segments();

    public static string Extension(WorkerRequest request)
    {
        var denied = request.DeniedCommands.Where(c => c.Trim().Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var text = new StringBuilder();
        text.Append("// Rendered by MUTHUR for one session. The deny list is the session's; editing this file changes nothing.\n");
        text.Append("export default function (pi: any) {\n");
        if (denied.Length == 0)
        {
            text.Append("  // No denied commands for this session: no handler.\n}\n");
            return text.ToString();
        }
        text.Append("  const denied: string[] = [");
        text.Append(string.Join(", ", denied.Select(d => "\"" + JsonEncodedText.Encode(d) + "\"")));
        text.Append("];\n");
        text.Append($$"""
              const toRegex = (glob: string) =>
                new RegExp("^" + glob.trim().split("*").map((s) => s.replace(/[.+?^${}()|[\]\\]/g, "\\$&")).join(".*") + "$", "s");
              const patterns = denied.map((glob) => ({ glob, regex: toRegex(glob) }));
              const segments = (command: string) =>
                command.split(/&&|\|\||;|\||\r?\n/).map((s) => s.trim()).filter((s) => s.length > 0);
              pi.on("tool_call", async (event: any) => {
                if (event.toolName === "bash" || event.toolName === "powershell") {
                  const command = String(event.input?.command ?? "");
                  for (const segment of segments(command))
                    for (const { glob, regex } of patterns)
                      if (regex.test(segment))
                        return { block: true, reason: `{{BlockMarker}} "${segment}" matches the denied pattern "${glob}". This session may not run it; do not retry it in another form.` };
                }
                if (event.toolName === "write" || event.toolName === "edit") {
                  const path = String(event.input?.path ?? "").replace(/\\/g, "/");
                  if (/(^|\/)\.git(\/|$)/.test(path))
                    return { block: true, reason: `{{BlockMarker}} writing "${path}": files under .git/ are changed through git commands, not edited.` };
                }
                return undefined;
              });
            }

            """);
        return text.ToString();
    }
}
