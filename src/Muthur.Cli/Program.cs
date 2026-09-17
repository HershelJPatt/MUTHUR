using System.CommandLine;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;

var root = new RootCommand("MUTHUR — control plane for an organization of coding agents. Output is JSON; exit codes: 0 ok, 2 rule violation, 3 conflict, 4 not running, 5 not found, 6 unauthorized.");
Globals.AddTo(root);
SystemCommands.AddTo(root);
AgentCommands.AddTo(root);
ProjectCommands.AddTo(root);
TaskCommands.AddTo(root);
RoleCommands.AddTo(root);
MessageCommands.AddTo(root);
WorkerCommands.AddTo(root);
KitCommands.AddTo(root);

return await root.Parse(args).InvokeAsync();
