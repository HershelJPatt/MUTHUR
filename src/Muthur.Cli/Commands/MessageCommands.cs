using System.CommandLine;
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

    private static void AddAsk(RootCommand root)
    {
        var question = new Argument<string>("question") { Description = "A concrete question only a founder can settle." };
        var task = new Option<string?>("--task") { Description = "The task this blocks; it moves to 'blocked' until answered." };
        var option = new Option<string[]>("--option") { Description = "An answer you propose (repeatable). Offer options whenever you can.", AllowMultipleArgumentsPerToken = true };
        var kind = new Option<string>("--kind") { DefaultValueFactory = _ => "human", Description = "human (default), or technical for delegated engineering judgment. Never technical for preferences, spending, permissions, account access, secrets or outbound approvals." };
        var ask = new Command("ask", "Ask for a decision; human requests remain founder-only.") { question, task, option, kind };
        ask.SetAction(async (parse, ct) => Output.Emit(parse, await HubClient.For(parse).PostAsync(Routes.Requests,
            new AskRequest(parse.GetValue(question)!, parse.GetValue(task), parse.GetValue(option), parse.GetValue(kind)!), MuthurJsonContext.Default.AskRequest, ct)));
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
