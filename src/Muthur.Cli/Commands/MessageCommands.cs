using System.CommandLine;
using System.Text.Json;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Commands;

public static class MessageCommands
{
    public static void AddTo(RootCommand root)
    {
        var msg = new Command("msg", "The internal bus: talk to agents, roles and the founder; wait for work on the inbox.");
        root.Subcommands.Add(msg);

        var to = new Option<string>("--to") { Description = "An agent name, role:<key>, or founder.", Required = true };
        var body = new Argument<string>("body");
        var blocking = new Option<bool>("--blocking") { Description = "You cannot continue until this is answered." };
        var task = new Option<string?>("--task") { Description = "Task this is about." };
        var send = new Command("send", "Send a message.") { to, body, blocking, task };
        send.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.Messages,
            new SendMessageRequest(parse.GetValue(to)!, parse.GetValue(body)!, parse.GetValue(blocking), parse.GetValue(task)),
            MuthurJsonContext.Default.SendMessageRequest, ct)));
        msg.Subcommands.Add(send);

        var wait = new Option<int>("--wait") { Description = "Block up to this many seconds (max 900) until a message for you or your roles arrives. Costs nothing while waiting." };
        var peek = new Option<bool>("--peek") { Description = "Do not mark messages as read." };
        var inbox = new Command("inbox", "Unread messages for you and the roles you hold. Reading marks them read.") { wait, peek };
        inbox.SetAction(async (parse, ct) =>
        {
            var seconds = Math.Clamp(parse.GetValue(wait), 0, 900);
            var query = $"?wait={seconds}" + (parse.GetValue(peek) ? "&peek=true" : "");
            return Output.Emit(parse, await HubClient.For(parse, TimeSpan.FromSeconds(seconds + 30)).GetAsync(Routes.Inbox + query, ct));
        });
        msg.Subcommands.Add(inbox);

        var limit = new Option<int?>("--limit");
        var founderOnly = new Option<bool>("--founder-thread") { Description = "Only messages to or from the founder." };
        var log = new Command("log", "Recent messages on the bus.") { limit, founderOnly };
        log.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(
            $"{Routes.Messages}?limit={parse.GetValue(limit) ?? 50}" + (parse.GetValue(founderOnly) ? "&founder=true" : ""), ct)));
        msg.Subcommands.Add(log);

        AddAsk(root);
    }

    /// <summary>The hub's cap on one inbox long-poll; a longer --wait is several of them back to back.</summary>
    internal const int InboxWaitMax = 900;

    /// <summary>How a wait of <paramref name="seconds"/> splits into inbox long-polls the hub accepts.</summary>
    internal static IEnumerable<int> WaitSlices(int seconds)
    {
        for (var left = seconds; left > 0; left -= InboxWaitMax) yield return Math.Min(left, InboxWaitMax);
    }

    /// <summary>
    /// Waits for the request to be answered by sleeping on the inbox — the hub messages the asker when a founder
    /// answers, so the poll wakes on the answer rather than on a timer — and re-reads the request after each wake.
    /// Returns the request as last seen, answered or not, or null when the hub could not be read.
    /// </summary>
    private static async Task<ApiResult?> AwaitAnswerAsync(HubClient hub, FounderRequestDto request, int seconds, CancellationToken ct)
    {
        ApiResult? latest = null;
        foreach (var slice in WaitSlices(seconds))
        {
            await hub.GetAsync(Routes.Inbox + $"?wait={slice}", ct);
            var listed = await hub.GetAsync(Routes.Requests + "?open=false", ct);
            if (!listed.IsSuccess) return latest;
            var current = JsonSerializer.Deserialize(listed.Body, MuthurJsonContext.Default.IReadOnlyListFounderRequestDto)?
                .FirstOrDefault(r => r.Id == request.Id);
            if (current is null) return latest;
            latest = new ApiResult(200, JsonSerializer.Serialize(current, MuthurJsonContext.Default.FounderRequestDto));
            if (!string.Equals(current.Status, "open", StringComparison.OrdinalIgnoreCase)) return latest;
        }
        return latest;
    }

    private static void AddAsk(RootCommand root)
    {
        var question = new Argument<string>("question") { Description = "A concrete decision for overseer triage or an explicitly reserved founder question." };
        var task = new Option<string?>("--task") { Description = "The task this blocks; it moves to 'blocked' until answered." };
        var option = new Option<string[]>("--option") { Description = "An answer you propose (repeatable). Offer options whenever you can.", AllowMultipleArgumentsPerToken = true };
        var kind = new Option<string>("--kind") { DefaultValueFactory = _ => "triage", Description = "triage (default): overseer classifies and directs engineering work; technical: delegated engineering judgment; human: explicit founder-only product, spending, permission, account, secret or outbound decision." };
        var wait = new Option<int>("--wait") { Description = "After asking, stay in this session up to this many seconds for the answer (a long-poll on your inbox; costs nothing while it waits). Prints the request with its status, answered or still open, and exits 0 either way." };
        var ask = new Command("ask", "Ask for a decision; human requests remain founder-only.") { question, task, option, kind, wait };
        ask.SetAction(async (parse, ct) =>
        {
            var seconds = Math.Max(0, parse.GetValue(wait));
            var hub = HubClient.For(parse, TimeSpan.FromSeconds(Math.Min(seconds, InboxWaitMax) + 30));
            var asked = await hub.PostAsync(Routes.Requests,
                new AskRequest(parse.GetValue(question)!, parse.GetValue(task), parse.GetValue(option), parse.GetValue(kind)!), MuthurJsonContext.Default.AskRequest, ct);
            if (!asked.IsSuccess || seconds == 0) return Output.Emit(parse, asked);
            var request = JsonSerializer.Deserialize(asked.Body, MuthurJsonContext.Default.FounderRequestDto);
            if (request is null) return Output.Emit(parse, asked);
            return Output.Emit(parse, await AwaitAnswerAsync(hub, request, seconds, ct) ?? asked);
        });
        root.Subcommands.Add(ask);

        var requests = new Command("requests", "Founder requests.");
        root.Subcommands.Add(requests);

        var all = new Option<bool>("--all") { Description = "Include answered and cancelled requests." };
        var list = new Command("list", "List founder requests (open ones by default).") { all };
        list.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).GetAsync(Routes.Requests + (parse.GetValue(all) ? "?open=false" : ""), ct)));
        requests.Subcommands.Add(list);

        var answerId = new Argument<int>("id");
        var answerText = new Argument<string>("answer");
        var answer = new Command("answer", "Answer a request. Needs --founder.") { answerId, answerText };
        answer.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(
            Routes.RequestAction(parse.GetValue(answerId), "answer"), new AnswerRequest(parse.GetValue(answerText)!), MuthurJsonContext.Default.AnswerRequest, ct)));
        requests.Subcommands.Add(answer);

        var cancelId = new Argument<int>("id");
        var cancel = new Command("cancel", "Withdraw a request you asked.") { cancelId };
        cancel.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.RequestAction(parse.GetValue(cancelId), "cancel"), ct)));
        requests.Subcommands.Add(cancel);
    }
}
