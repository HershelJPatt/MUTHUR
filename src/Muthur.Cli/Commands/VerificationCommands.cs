using System.CommandLine;
using System.Text.Json;
using Muthur.Launch;

namespace Muthur.Cli.Commands;

public static class VerificationCommands
{
    public static VerificationResult? ParseError(ParseResult parse) => parse.Errors.Count == 0 ? null :
        new(Guid.NewGuid(), "invalid", null, "bypassed", 2, string.Join(" ", parse.Errors.Select(e => e.Message)));

    public static void AddTo(RootCommand root) => AddTo(root, new ProcessRunner());

    internal static void AddTo(RootCommand root, IProcessRunner processes)
    {
        var verify = new Command("verify", "Run isolated local verification; evidence is not validation approval.");
        var repo = new Option<string>("--repo") { Required = true };
        var reference = new Option<string>("--ref") { Required = true };
        var spec = new Option<string>("--spec") { Required = true };
        var recipe = new Option<string>("--recipe") { Required = true };
        var output = new Option<string>("--output") { Required = true };
        var cache = new Option<string>("--cache") { Required = true };
        var task = new Option<string?>("--task");
        var parent = new Option<string?>("--parent-run");
        var subject = new Option<string?>("--subject");
        var timeout = new Option<int>("--timeout-minutes") { DefaultValueFactory = _ => 60 };
        var noCache = new Option<bool>("--no-cache");
        var run = new Command("run", "Build, run the full tests, prepare and smoke an isolated installed CLI.")
            { repo, reference, spec, recipe, output, cache, task, parent, subject, timeout, noCache };
        run.SetAction(async (parse, ct) =>
        {
            var result = await new VerificationRunner(processes).RunAsync(new(parse.GetValue(repo)!, parse.GetValue(reference)!,
                parse.GetValue(spec)!, parse.GetValue(recipe)!, parse.GetValue(output)!, parse.GetValue(cache)!,
                parse.GetValue(task), parse.GetValue(parent), parse.GetValue(subject), parse.GetValue(timeout), parse.GetValue(noCache)), ct);
            Console.WriteLine(JsonSerializer.Serialize(result, VerificationJsonContext.Default.VerificationResult));
            return result.ExitCode;
        });
        var cleanupOutput = new Option<string>("--output") { Required = true };
        var cleanup = new Command("cleanup", "Recover an interrupted run using its recorded ownership; refuses active runners.") { cleanupOutput };
        cleanup.SetAction(async (parse, ct) =>
        {
            VerificationResult result;
            try { result = await new VerificationRunner(processes).CleanupAsync(parse.GetValue(cleanupOutput)!, ct); }
            catch (OperationCanceledException) { result = new(Guid.NewGuid(), "cancelled", null, "bypassed", 130); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
            { result = new(Guid.NewGuid(), "failed", null, "bypassed", 1, ex.Message); }
            Console.WriteLine(JsonSerializer.Serialize(result, VerificationJsonContext.Default.VerificationResult));
            return result.ExitCode;
        });
        verify.Subcommands.Add(run);
        verify.Subcommands.Add(cleanup);
        var fixtureOutput = new Option<string>("--output") { Required = true };
        var barrier = new Option<string>("--barrier") { Required = true };
        var fixture = new Command("containment-fixture", "Bounded installed containment diagnostic; never recipe success.") { fixtureOutput, barrier };
        fixture.SetAction(async (parse, ct) => await VerificationContainmentFixture.RunAsync(parse.GetValue(fixtureOutput)!, parse.GetValue(barrier)!, ct));
        verify.Subcommands.Add(fixture);
        root.Subcommands.Add(verify);
    }
}
