using System.CommandLine;
using Muthur.Cli.Infrastructure;
using Muthur.Contracts;

namespace Muthur.Cli.Commands;

public static class DesignCommands
{
    public static void AddTo(RootCommand root)
    {
        var group = new Command("design", "Version and approve committed visual artifacts bound to a frozen spec.");
        root.Subcommands.Add(group);
        var task = new Argument<string>("task");
        var show = new Command("show", "Show artifact Git identities, approval history and independent comparisons.") { task };
        show.SetAction(async (p, ct) => Output.Emit(p, await HubClient.For(p).GetAsync("/api/v1/designs/" + Uri.EscapeDataString(p.GetValue(task)!), ct)));
        group.Subcommands.Add(show);
        KnowledgeCommands.Write(group, "attach", MuthurJsonContext.Default.WriteDesignRequest, route: "/api/v1/designs");
        KnowledgeCommands.Write(group, "approve", MuthurJsonContext.Default.ApproveDesignRequest, route: "/api/v1/designs");
        KnowledgeCommands.Write(group, "check", MuthurJsonContext.Default.CheckDesignRequest, route: "/api/v1/designs");
    }
}
