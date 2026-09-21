using System.CommandLine;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Commands;

public static class RoleCommands
{
    public static void AddTo(RootCommand root)
    {
        var role = new Command("role", "Standing roles (validators, on-calls): held as leases, described by briefs.");
        root.Subcommands.Add(role);

        var list = new Command("list", "List roles and who holds them.");
        list.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.Roles, ct)));
        role.Subcommands.Add(list);

        var defineKey = new Argument<string>("role") { Description = "Role key, e.g. win-validator, comms-oncall." };
        var briefFile = new Option<string?>("--brief-file") { Description = "Markdown file with the role's brief (its job description)." };
        var validator = new Option<bool?>("--validator") { Description = "Whether holders may give validation verdicts (default: true for keys ending in -validator)." };
        var holders = new Option<int?>("--holders") { Description = "How many agents may hold this role at once (default 1). Only a validator role may exceed 1." };
        var define = new Command("define", "Create a role or replace its brief. Needs --founder.") { defineKey, briefFile, validator, holders };
        define.SetAction(async (parse, ct) =>
        {
            string? brief = null;
            if (parse.GetValue(briefFile) is { } file)
            {
                if (FileProvenance.IsDirty(file))
                    return Output.Error("brief_file_dirty",
                        $"{file} has no committed version, so the brief you install will match no commit. Commit it, or pass the text you mean.",
                        ExitCodes.RuleViolation);
                brief = await File.ReadAllTextAsync(file, ct);
                // stderr, never stdout: stdout is the JSON an agent parses.
                if (FileProvenance.Describe(file) is { } source)
                    Console.Error.WriteLine($"Read {file} at {source.Ref} ({source.Commit}).");
            }
            var request = new DefineRoleRequest(parse.GetValue(defineKey)!, brief, parse.GetValue(validator), parse.GetValue(holders));
            return Output.Emit(parse, await HubClient.For(parse).PutAsync(Routes.Roles, request, MuthurJsonContext.Default.DefineRoleRequest, ct));
        });
        role.Subcommands.Add(define);

        var briefKey = new Argument<string>("role");
        var raw = new Option<bool>("--raw") { Description = "Print only the brief's markdown." };
        var brief = new Command("brief", "Read a role's brief. Read it every time you take the role.") { briefKey, raw };
        brief.SetAction(async (parse, ct) =>
        {
            var result = await HubClient.For(parse).GetAsync(Routes.RoleAction(parse.GetValue(briefKey)!, "brief"), ct);
            if (!result.IsSuccess || !parse.GetValue(raw)) return Output.Emit(parse, result);
            var dto = System.Text.Json.JsonSerializer.Deserialize(result.Body, MuthurJsonContext.Default.RoleBriefDto)!;
            Console.Out.WriteLine(dto.Brief);
            return ExitCodes.Ok;
        });
        role.Subcommands.Add(brief);

        var takeKey = new Argument<string>("role");
        var take = new Command("take", "Take a role. Exit 3 if another live agent holds it. Any hub call renews your hold.") { takeKey };
        take.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.RoleAction(parse.GetValue(takeKey)!, "take"), ct)));
        role.Subcommands.Add(take);

        var releaseKey = new Argument<string>("role");
        var release = new Command("release", "Give a role up.") { releaseKey };
        release.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.RoleAction(parse.GetValue(releaseKey)!, "release"), ct)));
        role.Subcommands.Add(release);

        AddValidate(root);
    }

    private static void AddValidate(RootCommand root)
    {
        var validate = new Command("validate", "Validation verdicts. Requires holding the validator role.");
        root.Subcommands.Add(validate);

        var roleFilter = new Option<string?>("--role") { Description = "Only tasks waiting on this validator role." };
        var all = new Option<bool>("--all") { Description = "Include tasks another validator has already claimed." };
        var list = new Command("list", "Tasks waiting for validation.") { roleFilter, all };
        list.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(
            Routes.Validations + ValidationListQuery(parse.GetValue(roleFilter), parse.GetValue(all)), ct)));
        validate.Subcommands.Add(list);

        var queue = new Command("queue", "How deep the validation queue is, per validator role.");
        queue.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.ValidationQueue, ct)));
        validate.Subcommands.Add(queue);

        validate.Subcommands.Add(Claim("claim", "validate-claim",
            "Take a task for validation so no other validator spends a session on it. Exit 3 if someone already has it."));
        validate.Subcommands.Add(Claim("release", "validate-release", "Give back a task you claimed but will not validate."));

        validate.Subcommands.Add(Verdict("pass", "The task works end to end on your platform."));
        validate.Subcommands.Add(Verdict("fail", "The task does not pass; it returns to its owner with your evidence."));
        validate.Subcommands.Add(Verdict("blocked", "You could not validate it at all — say what stopped you. The task returns to its owner; this is not a verdict on the work."));
    }

    /// <summary>
    /// The query behind `validate list`. Both filters are optional and either may be the first one present,
    /// so the leading '?' belongs to the string as a whole rather than to `--role`.
    /// </summary>
    internal static string ValidationListQuery(string? role, bool all)
    {
        var query = new List<string>();
        if (role is { } r) query.Add("role=" + Uri.EscapeDataString(r));
        if (all) query.Add("all=true");
        return query.Count == 0 ? "" : "?" + string.Join('&', query);
    }

    private static Command Claim(string name, string action, string description)
    {
        var id = new Argument<string>("id") { Description = "Task id, e.g. T-12." };
        var asRole = new Option<string>("--as") { Description = "The validator role you are acting as.", Required = true };
        var subject = new Option<Guid?>("--subject") { Description = "Expected validation round; omitted to claim the current round." };
        var command = new Command(name, description) { id, asRole, subject };
        command.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(
            Routes.TaskAction(parse.GetValue(id)!, action), new ClaimValidationRequest(parse.GetValue(asRole)!, parse.GetValue(subject)), MuthurJsonContext.Default.ClaimValidationRequest, ct)));
        return command;
    }

    private static Command Verdict(string name, string description)
    {
        var id = new Argument<string>("id") { Description = "Task id, e.g. T-12." };
        var asRole = new Option<string>("--as") { Description = "The validator role you are acting as.", Required = true };
        var evidence = new Option<string?>("--evidence") { Description = "Report describing check, observation and artifact/reference or reproduction command." };
        var evidenceFile = new Option<string?>("--evidence-file") { Description = "Read the report from a local UTF-8 file." };
        var note = new Option<string?>("--note") { Description = "Alias for an inline evidence report." };
        var subject = new Option<Guid?>("--subject") { Description = "Round ID retained from the claim; required for every verdict." };
        var command = new Command(name, description) { id, asRole, evidence, evidenceFile, note, subject };
        if (name == "pass") command.Aliases.Add("yes");
        if (name == "fail") command.Aliases.Add("no");
        command.SetAction(async (parse, ct) =>
        {
            if (new[] { parse.GetResult(evidence), parse.GetResult(evidenceFile), parse.GetResult(note) }.Count(x => x is not null) > 1)
                return Output.Error("evidence_conflict", "Use only one of --evidence, --evidence-file or --note.", ExitCodes.RuleViolation);
            string? text;
            try
            {
                text = parse.GetValue(evidenceFile) is { } file
                    ? await File.ReadAllTextAsync(file, new System.Text.UTF8Encoding(false, true), ct)
                    : parse.GetValue(evidence) ?? parse.GetValue(note);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
            {
                return Output.Error("evidence_file_unreadable", "Read a local UTF-8 report with --evidence-file: " + ex.Message, ExitCodes.RuleViolation);
            }
            return Output.Emit(parse, await HubClient.For(parse).PostAsync(
                Routes.TaskAction(parse.GetValue(id)!, name), new VerdictRequest(parse.GetValue(asRole)!, text, parse.GetValue(subject)), MuthurJsonContext.Default.VerdictRequest, ct));
        });
        return command;
    }
}
