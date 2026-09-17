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
        var define = new Command("define", "Create a role or replace its brief. Needs --founder.") { defineKey, briefFile, validator };
        define.SetAction(async (parse, ct) =>
        {
            var brief = parse.GetValue(briefFile) is { } file ? await File.ReadAllTextAsync(file, ct) : null;
            var request = new DefineRoleRequest(parse.GetValue(defineKey)!, brief, parse.GetValue(validator));
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
        var list = new Command("list", "Tasks waiting for validation.") { roleFilter };
        list.SetAction(async (parse, ct) =>
        {
            var query = parse.GetValue(roleFilter) is { } r ? "?role=" + Uri.EscapeDataString(r) : "";
            return Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.Validations + query, ct));
        });
        validate.Subcommands.Add(list);

        validate.Subcommands.Add(Verdict("pass", "The task works end to end on your platform."));
        validate.Subcommands.Add(Verdict("fail", "The task does not pass; it returns to its owner with your evidence."));
    }

    private static Command Verdict(string name, string description)
    {
        var id = new Argument<string>("id") { Description = "Task id, e.g. T-12." };
        var asRole = new Option<string>("--as") { Description = "The validator role you are acting as.", Required = true };
        var evidence = new Option<string?>("--evidence") { Description = "File with what you ran and saw (required for fail)." };
        var note = new Option<string?>("--note") { Description = "Inline evidence instead of a file." };
        var command = new Command(name, description) { id, asRole, evidence, note };
        command.SetAction(async (parse, ct) =>
        {
            var text = parse.GetValue(evidence) is { } file ? await File.ReadAllTextAsync(file, ct) : parse.GetValue(note);
            return Output.Emit(parse, await HubClient.For(parse).PostAsync(
                Routes.TaskAction(parse.GetValue(id)!, name), new VerdictRequest(parse.GetValue(asRole)!, text), MuthurJsonContext.Default.VerdictRequest, ct));
        });
        return command;
    }
}
