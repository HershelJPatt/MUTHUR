using System.CommandLine;
using Muthur.Cli.Commands;
using Muthur.Cli.Infrastructure;

// Piped output on Windows defaults to the OEM code page, which corrupts non-ASCII JSON for every consumer.
Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

var root = new RootCommand("MUTHUR — control plane for an organization of coding agents. Output is JSON; exit codes: 0 ok, 2 rule violation, 3 conflict, 4 not running, 5 not found, 6 unauthorized.");
Globals.AddTo(root);
SystemCommands.AddTo(root);
AgentCommands.AddTo(root);
ProjectCommands.AddTo(root);
TaskCommands.AddTo(root);
RoleCommands.AddTo(root);
MessageCommands.AddTo(root);
WorkerCommands.AddTo(root);
InboundCommands.AddTo(root);
KitCommands.AddTo(root);

return await root.Parse(args).InvokeAsync();
