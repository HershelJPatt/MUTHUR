using System.CommandLine;
using System.Text.Json;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Commands;

public static class OutboundCommands
{
    public static void AddTo(RootCommand root)
    {
        var outbound = new Command("out", "The only way out of the machine: allowlisted targets, secret scan, peer review of the exact bytes, founder approval where required.");
        root.Subcommands.Add(outbound);

        var targets = new Command("targets", "The allowlist: where the organization may send.");
        targets.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.OutboundTargets, ct)));
        outbound.Subcommands.Add(targets);

        var targetKey = new Argument<string>("key");
        var channel = new Option<string>("--channel") { Description = "file | discord-webhook | github-issue", Required = true };
        var address = new Option<string>("--address") { Description = "Absolute file path, webhook URL, or owner/repo#<issue>.", Required = true };
        var founderApproval = new Option<bool>("--founder-approval") { Description = "Every message to this target also needs the founder's approval." };
        var target = new Command("target", "Add or change an allowed target. Needs --founder.") { targetKey, channel, address, founderApproval };
        target.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PutAsync(Routes.OutboundTargets,
            new DefineTargetRequest(parse.GetValue(targetKey)!, parse.GetValue(channel)!, parse.GetValue(address)!, parse.GetValue(founderApproval)),
            MuthurJsonContext.Default.DefineTargetRequest, ct)));
        outbound.Subcommands.Add(target);

        var to = new Option<string>("--target") { Description = "Key of an allowed target (muthur out targets).", Required = true };
        var file = new Option<string?>("--file") { Description = "File holding the exact text to send." };
        var text = new Option<string?>("--text") { Description = "The text, inline." };
        var task = new Option<string?>("--task");
        var draft = new Command("draft", "Submit text for sending. Exit 2 if the target is not allowed or the text contains a credential.") { to, file, text, task };
        draft.SetAction(async (parse, ct) =>
        {
            var body = parse.GetValue(file) is { } path ? await File.ReadAllTextAsync(path, ct) : parse.GetValue(text);
            if (string.IsNullOrWhiteSpace(body)) return Output.Error("body_required", "Pass --file or --text.", ExitCodes.RuleViolation);
            return Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.Outbound,
                new DraftOutboundRequest(parse.GetValue(to)!, body, parse.GetValue(task)), MuthurJsonContext.Default.DraftOutboundRequest, ct));
        });
        outbound.Subcommands.Add(draft);

        var status = new Option<string?>("--status") { Description = "pending_review | rejected | awaiting_founder | approved | sent | failed | all" };
        var list = new Command("list", "List outbound messages.") { status };
        list.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(
            Routes.Outbound + (parse.GetValue(status) is { } s ? "?status=" + Uri.EscapeDataString(s) : ""), ct)));
        outbound.Subcommands.Add(list);

        var showId = Id();
        var show = new Command("show", "Show a message: the exact body and its sha256. Read it before you review it.") { showId };
        show.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync($"{Routes.Outbound}/{Uri.EscapeDataString(parse.GetValue(showId)!)}", ct)));
        outbound.Subcommands.Add(show);

        var reviewId = Id();
        var approve = new Option<bool>("--approve");
        var reject = new Option<bool>("--reject");
        var sha = new Option<string>("--sha") { Description = "sha256 of the body you read (from `muthur out show`). Proves which bytes you reviewed.", Required = true };
        var note = new Option<string?>("--note") { Description = "Required when rejecting: what to change." };
        var review = new Command("review", "Review another agent's message. You cannot review your own.") { reviewId, approve, reject, sha, note };
        review.SetAction(async (parse, ct) =>
        {
            if (parse.GetValue(approve) == parse.GetValue(reject))
                return Output.Error("verdict_required", "Pass exactly one of --approve or --reject.", ExitCodes.RuleViolation);
            return Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.OutboundAction(parse.GetValue(reviewId)!, "review"),
                new ReviewOutboundRequest(parse.GetValue(approve), parse.GetValue(sha)!, parse.GetValue(note)), MuthurJsonContext.Default.ReviewOutboundRequest, ct));
        });
        outbound.Subcommands.Add(review);

        var approveId = Id();
        var founderApprove = new Command("approve", "Founder approval for targets that require it. Needs --founder.") { approveId };
        founderApprove.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.OutboundAction(parse.GetValue(approveId)!, "approve"), ct)));
        outbound.Subcommands.Add(founderApprove);

        var declineId = Id();
        var declineNote = new Option<string?>("--note");
        var decline = new Command("decline", "Founder declines a message. Needs --founder.") { declineId, declineNote };
        decline.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.OutboundAction(parse.GetValue(declineId)!, "decline"),
            new DeclineOutboundRequest(parse.GetValue(declineNote)), MuthurJsonContext.Default.DeclineOutboundRequest, ct)));
        outbound.Subcommands.Add(decline);

        var sendId = Id();
        var send = new Command("send", "Send an approved message. The hub re-checks everything and transmits; exit 3 if delivery fails.") { sendId };
        send.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse, TimeSpan.FromMinutes(3)).PostAsync(Routes.OutboundAction(parse.GetValue(sendId)!, "send"), ct)));
        outbound.Subcommands.Add(send);

        var retryId = Id();
        var retry = new Command("retry", "Retry a failed delivery (same bytes, no new review).") { retryId };
        retry.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse, TimeSpan.FromMinutes(3)).PostAsync(Routes.OutboundAction(parse.GetValue(retryId)!, "retry"), ct)));
        outbound.Subcommands.Add(retry);
    }

    private static Argument<string> Id() => new("id") { Description = "Outbound id, e.g. O-3." };
}
