using System.CommandLine;
using System.Text.Json.Serialization.Metadata;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Commands;

public static class IncidentCommands
{
    public static void AddTo(RootCommand root)
    {
        var incident = new Command("incident", "Shared observations and explicitly evidenced assignment gates. Output is JSON.");
        root.Subcommands.Add(incident);

        var title = new Argument<string>("title");
        var project = Flag("project");
        var signature = Flag("signature", true);
        var path = Flag("path", true);
        var configuration = Flag("configuration", true);
        var recovery = Flag("recovery", true);
        var add = new Command("add", "Create a suspected incident.") { title, project, signature, path, configuration, recovery };
        add.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.Incidents,
            new AddIncidentRequest(parse.GetValue(title)!, parse.GetValue(signature)!, parse.GetValue(path)!, parse.GetValue(configuration)!,
                parse.GetValue(recovery)!, parse.GetValue(project) ?? ProjectContext.FindKey()), MuthurJsonContext.Default.AddIncidentRequest, ct)));
        incident.Subcommands.Add(add);

        var listProject = Flag("project");
        var list = new Command("list", "List incidents in ID order, including terminal incidents.") { listProject };
        list.SetAction(async (parse, ct) =>
        {
            var key = parse.GetValue(listProject) ?? ProjectContext.FindKey();
            return Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.Incidents + (key is null ? "" : "?project=" + Uri.EscapeDataString(key)), ct));
        });
        incident.Subcommands.Add(list);

        var showId = Id();
        var show = new Command("show", "Show retained observations, suppressions, and incident events.") { showId };
        show.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.Incident(parse.GetValue(showId)!), ct)));
        incident.Subcommands.Add(show);

        var matchProject = Flag("project");
        var matchSignature = Flag("signature", true);
        var matchPath = Flag("path", true);
        var matchConfiguration = Flag("configuration", true);
        var match = new Command("match", "Read-only exact matches; advisory, never automatically links or suppresses.") { matchProject, matchSignature, matchPath, matchConfiguration };
        match.SetAction(async (parse, ct) =>
        {
            var query = MatchQuery(parse.GetValue(matchProject) ?? ProjectContext.FindKey(), parse.GetValue(matchSignature)!,
                parse.GetValue(matchPath)!, parse.GetValue(matchConfiguration)!);
            return Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.IncidentMatches + query, ct));
        });
        incident.Subcommands.Add(match);

        var diagnosis = Flag("diagnosis", true);
        var workaround = Flag("workaround");
        var authorization = Flag("authorization");
        Mutation("update", "Record diagnosis and authorized workaround documentation.", [diagnosis, workaround, authorization],
            p => new UpdateIncidentRequest(p.GetValue(diagnosis)!, p.GetValue(workaround), p.GetValue(authorization)), MuthurJsonContext.Default.UpdateIncidentRequest);
        var state = Flag("state", true);
        var transitionEvidence = Flag("evidence", true);
        Mutation("transition", "Transition with explicit evidence.", [state, transitionEvidence],
            p => new TransitionIncidentRequest(p.GetValue(state)!, p.GetValue(transitionEvidence)!), MuthurJsonContext.Default.TransitionIncidentRequest);
        var task = Flag("task", true);
        var evidence = Flag("evidence", true);
        var observeSignature = Flag("signature", true);
        var observePath = Flag("path", true);
        var observeConfiguration = Flag("configuration", true);
        var run = Flag("run");
        Mutation("observe", "Append independent task evidence; differing tuples are retained.", [task, evidence, observeSignature, observePath, observeConfiguration, run],
            p => new ObserveIncidentRequest(p.GetValue(task)!, p.GetValue(evidence)!, p.GetValue(observeSignature)!, p.GetValue(observePath)!, p.GetValue(observeConfiguration)!, p.GetValue(run)), MuthurJsonContext.Default.ObserveIncidentRequest);
        var observation = new Option<int>("--observation") { Required = true };
        var unlinkReason = Flag("reason", true);
        Mutation("unlink", "Correct a grouping while retaining its evidence.", [observation, unlinkReason],
            p => new UnlinkIncidentRequest(p.GetValue(observation), p.GetValue(unlinkReason)!), MuthurJsonContext.Default.UnlinkIncidentRequest);
        var suppressTask = Flag("task", true);
        var assignment = Flag("assignment", true);
        var suppressObservation = new Option<int>("--observation") { Required = true };
        var suppressReason = Flag("reason", true);
        Mutation("suppress", "Suppress only the specified task/assignment with exact current evidence.", [suppressTask, assignment, suppressObservation, suppressReason],
            p => new SuppressIncidentRequest(p.GetValue(suppressTask)!, p.GetValue(assignment)!, p.GetValue(suppressObservation), p.GetValue(suppressReason)!), MuthurJsonContext.Default.SuppressIncidentRequest);
        var kind = Flag("kind", true);
        var recoverEvidence = Flag("evidence", true);
        var recoverConfiguration = Flag("configuration");
        Mutation("recover", "Record recovery. Probe attests a successful bounded probe for this exact condition; configuration records a measured change. Never executes a probe.", [kind, recoverEvidence, recoverConfiguration],
            p => new RecoverIncidentRequest(p.GetValue(kind)!, p.GetValue(recoverEvidence)!, p.GetValue(recoverConfiguration)), MuthurJsonContext.Default.RecoverIncidentRequest);

        var metricsId = Id();
        var hours = new Option<int>("--hours") { DefaultValueFactory = _ => 24, Description = "Window in hours, 1..720." };
        var metrics = new Command("metrics", "Recorded evidence and cohort activity; missing attribution is explicit.") { metricsId, hours };
        metrics.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.IncidentAction(parse.GetValue(metricsId)!, "metrics") + "?hours=" + parse.GetValue(hours), ct)));
        incident.Subcommands.Add(metrics);

        void Mutation<T>(string name, string description, Option[] flags, Func<ParseResult, T> request, JsonTypeInfo<T> json)
        {
            var id = Id();
            var command = new Command(name, description) { id };
            foreach (var flag in flags) command.Options.Add(flag);
            command.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(
                Routes.IncidentAction(parse.GetValue(id)!, name), request(parse), json, ct)));
            incident.Subcommands.Add(command);
        }
    }

    private static Option<string?> Flag(string name, bool required = false) => new("--" + name) { Required = required };
    private static Argument<string> Id() => new("id") { Description = "Incident identifier, e.g. I-7." };

    public static string MatchQuery(string? project, string signature, string path, string configuration)
    {
        var query = new List<string>();
        if (project is not null) query.Add("project=" + Uri.EscapeDataString(project));
        query.Add("signature=" + Uri.EscapeDataString(signature));
        query.Add("path=" + Uri.EscapeDataString(path));
        query.Add("configuration=" + Uri.EscapeDataString(configuration));
        return "?" + string.Join('&', query);
    }
}
