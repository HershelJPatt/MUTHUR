using System.CommandLine;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Commands;

public static class ProjectCommands
{
    public static void AddTo(RootCommand root)
    {
        var project = new Command("project", "Projects: a repository plus its landing and validation policy. Changes need --founder.");
        root.Subcommands.Add(project);

        var key = new Argument<string>("key") { Description = "Short project key, e.g. muthur." };
        var repo = new Option<string?>("--repo") { Description = "Path of the repository." };
        var name = new Option<string?>("--name") { Description = "Display name." };
        var branch = new Option<string?>("--branch") { Description = "Default branch (main)." };
        var land = new Option<string?>("--land") { Description = "merge: the hub merges. pr: the hub opens a pull request and a human merges." };
        var validator = new Option<string[]>("--validator") { Description = "Validator role that must pass before landing (repeatable).", AllowMultipleArgumentsPerToken = true };

        var add = new Command("add", "Create a project.") { key, repo, name, branch, land, validator };
        add.SetAction(async (parse, ct) =>
        {
            if (!TryLandMode(parse.GetValue(land), out var mode, out var error)) return error;
            var request = new AddProjectRequest(parse.GetValue(key)!, Path.GetFullPath(parse.GetValue(repo) ?? "."), parse.GetValue(name),
                parse.GetValue(branch), mode, parse.GetValue(validator));
            return Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.Projects, request, MuthurJsonContext.Default.AddProjectRequest, ct));
        });
        project.Subcommands.Add(add);

        var setKey = new Argument<string>("key");
        var setValidator = new Option<string[]?>("--validator") { Description = "Replaces the validator list (repeatable). Use --no-validators to clear.", AllowMultipleArgumentsPerToken = true };
        var noValidators = new Option<bool>("--no-validators") { Description = "Require no validators." };
        var set = new Command("set", "Change a project's settings.") { setKey, repo, name, branch, land, setValidator, noValidators };
        set.SetAction(async (parse, ct) =>
        {
            if (!TryLandMode(parse.GetValue(land), out var mode, out var error)) return error;
            var validators = parse.GetValue(noValidators) ? [] : parse.GetValue(setValidator) is { Length: > 0 } v ? v : null;
            var request = new UpdateProjectRequest(parse.GetValue(repo) is { } r ? Path.GetFullPath(r) : null, parse.GetValue(name), parse.GetValue(branch), mode, validators);
            return Output.Emit(parse, await HubClient.For(parse).PutAsync(Routes.Project(parse.GetValue(setKey)!), request, MuthurJsonContext.Default.UpdateProjectRequest, ct));
        });
        project.Subcommands.Add(set);

        var list = new Command("list", "List projects.");
        list.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.Projects, ct)));
        project.Subcommands.Add(list);

        var showKey = new Argument<string?>("key") { Description = "Defaults to the project of the current directory.", Arity = ArgumentArity.ZeroOrOne };
        var show = new Command("show", "Show one project.") { showKey };
        show.SetAction(async (parse, ct) =>
        {
            var k = parse.GetValue(showKey) ?? ProjectContext.FindKey();
            return k is null
                ? Output.Error("project_required", $"Pass a project key, or run inside a repository with {ProjectContext.FileName}.", ExitCodes.RuleViolation)
                : Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.Project(k), ct));
        });
        project.Subcommands.Add(show);
    }

    private static bool TryLandMode(string? text, out LandMode? mode, out int error)
    {
        mode = null;
        error = 0;
        if (text is null) return true;
        if (Wire.TryParseLandMode(text, out var parsed))
        {
            mode = parsed;
            return true;
        }
        error = Output.Error("invalid_land_mode", "--land must be 'merge' or 'pr'.", ExitCodes.RuleViolation);
        return false;
    }
}
