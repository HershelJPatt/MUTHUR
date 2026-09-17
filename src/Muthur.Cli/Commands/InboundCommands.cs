using System.CommandLine;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Commands;

public static class InboundCommands
{
    public static void AddTo(RootCommand root)
    {
        var inbound = new Command("inbound", "What arrived from outside (issues, mail, chat) and has not been decided on yet.");
        root.Subcommands.Add(inbound);

        var status = new Option<string?>("--status") { Description = "unclaimed (default) | claimed | converted | dismissed | all" };
        var project = new Option<string?>("--project");
        var limit = new Option<int?>("--limit");
        var list = new Command("list", "List inbound items.") { status, project, limit };
        list.SetAction(async (parse, ct) =>
        {
            var query = new List<string>();
            if (parse.GetValue(status) is { } s) query.Add("status=" + Uri.EscapeDataString(s));
            if (parse.GetValue(project) is { } p) query.Add("project=" + Uri.EscapeDataString(p));
            if (parse.GetValue(limit) is { } l) query.Add("limit=" + l);
            return Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.Inbound + "?" + string.Join('&', query), ct));
        });
        inbound.Subcommands.Add(list);

        var showId = Id();
        var show = new Command("show", "Show one inbound item with its full body.") { showId };
        show.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync($"{Routes.Inbound}/{Uri.EscapeDataString(parse.GetValue(showId)!)}", ct)));
        inbound.Subcommands.Add(show);

        var claimId = Id();
        var asTask = new Option<bool>("--as-task") { Description = "Also turn it into a backlog task in one step." };
        var priority = new Option<int>("--priority");
        var title = new Option<string?>("--title") { Description = "Task title (default: the item's title)." };
        var claim = new Command("claim", "Take responsibility for an item. Exit 3 if someone else already has it.") { claimId, asTask, priority, title };
        claim.SetAction(async (parse, ct) =>
        {
            var hub = HubClient.For(parse);
            var id = parse.GetValue(claimId)!;
            var claimed = await hub.PostAsync(Routes.InboundAction(id, "claim"), ct);
            if (!claimed.IsSuccess || !parse.GetValue(asTask)) return Output.Emit(parse, claimed);
            return Output.Emit(parse, await hub.PostAsync(Routes.InboundAction(id, "convert"),
                new ConvertInboundRequest(parse.GetValue(priority), parse.GetValue(title)), MuthurJsonContext.Default.ConvertInboundRequest, ct));
        });
        inbound.Subcommands.Add(claim);

        var convertId = Id();
        var convertPriority = new Option<int>("--priority");
        var convertTitle = new Option<string?>("--title");
        var convert = new Command("convert", "Turn an item into a backlog task.") { convertId, convertPriority, convertTitle };
        convert.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.InboundAction(parse.GetValue(convertId)!, "convert"),
            new ConvertInboundRequest(parse.GetValue(convertPriority), parse.GetValue(convertTitle)), MuthurJsonContext.Default.ConvertInboundRequest, ct)));
        inbound.Subcommands.Add(convert);

        var dismissId = Id();
        var reason = new Option<string>("--reason") { Description = "Why this needs no action.", Required = true };
        var dismiss = new Command("dismiss", "Close an item without a task.") { dismissId, reason };
        dismiss.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.InboundAction(parse.GetValue(dismissId)!, "dismiss"),
            new DismissInboundRequest(parse.GetValue(reason)!), MuthurJsonContext.Default.DismissInboundRequest, ct)));
        inbound.Subcommands.Add(dismiss);

        var source = new Option<string>("--source") { Description = "Where it came from, e.g. email, discord, manual.", DefaultValueFactory = _ => "manual" };
        var externalId = new Option<string?>("--external-id") { Description = "Identity within the source; re-adding the same one is a no-op (default: a new id)." };
        var addTitle = new Argument<string>("title");
        var body = new Option<string?>("--body");
        var bodyFile = new Option<string?>("--body-file");
        var url = new Option<string?>("--url");
        var author = new Option<string?>("--author");
        var addProject = new Option<string?>("--project");
        var add = new Command("add", "Push an item into the hub from any tool or script.") { addTitle, source, externalId, body, bodyFile, url, author, addProject };
        add.SetAction(async (parse, ct) =>
        {
            var text = parse.GetValue(bodyFile) is { } file ? await File.ReadAllTextAsync(file, ct) : parse.GetValue(body);
            var request = new AddInboundRequest(parse.GetValue(source)!, parse.GetValue(externalId) ?? Guid.NewGuid().ToString("n"), parse.GetValue(addTitle)!,
                text, parse.GetValue(url), parse.GetValue(author), parse.GetValue(addProject) ?? ProjectContext.FindKey());
            return Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.Inbound, request, MuthurJsonContext.Default.AddInboundRequest, ct));
        });
        inbound.Subcommands.Add(add);

        var sources = new Command("sources", "Configured ingest sources with their cursors and last errors. Configure with: muthur project set <key> --ingest github:owner/repo");
        sources.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.IngestSources, ct)));
        inbound.Subcommands.Add(sources);

        var poll = new Command("poll", "Poll every ingest source now (the hub also does this on start and every few minutes).");
        poll.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse, TimeSpan.FromMinutes(3)).PostAsync(Routes.IngestPoll, ct)));
        inbound.Subcommands.Add(poll);
    }

    private static Argument<string> Id() => new("id") { Description = "Inbound id, e.g. I-7." };
}
