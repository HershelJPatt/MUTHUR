using System.CommandLine;
using System.Text.Json;
using Muthur.Cli.Commands;
using Muthur.Contracts;

namespace Muthur.Cli.Tests;

/// <summary>
/// `muthur conductor sessions` and `muthur conductor unattended` judge nothing — the hub owns every rule about
/// what a legal ceiling is, so a CLI that refused a number here would only be a second opinion to keep in step.
/// What is the CLI's own is where each value lands on the wire, and that is what these pin: the window's
/// `--sessions` is not the standing ceiling, and a hub told the wrong one would obey it without complaint.
/// </summary>
public sealed class ConductorCeilingTests
{
    /// <summary>The real command tree, and the subcommand the founder actually types.</summary>
    private static (RootCommand Root, Command Command) Tree(string name)
    {
        var root = new RootCommand("test");
        WorkerCommands.AddTo(root);
        return (root, root.Subcommands.Single(c => c.Name == "conductor").Subcommands.Single(c => c.Name == name));
    }

    private static ConductorSessionsRequest ParseSessions(params string[] args)
    {
        var (root, sessions) = Tree("sessions");
        var count = (Argument<int>)sessions.Arguments.Single(a => a.Name == "count");
        return WorkerCommands.SessionsRequest(root.Parse(args).GetValue(count));
    }

    private static ConductorSessionsRequest ParseUnattended(params string[] args)
    {
        var (root, unattended) = Tree("unattended");
        var from = (Option<string?>)unattended.Options.Single(o => o.Name == "--from");
        var to = (Option<string?>)unattended.Options.Single(o => o.Name == "--to");
        var inWindow = (Option<int?>)unattended.Options.Single(o => o.Name == "--sessions");
        var clear = (Option<bool>)unattended.Options.Single(o => o.Name == "--clear");

        var parse = root.Parse(args);
        return WorkerCommands.UnattendedRequest(parse.GetValue(from), parse.GetValue(to), parse.GetValue(inWindow), parse.GetValue(clear));
    }

    [Fact]
    public void Both_commands_hang_off_the_conductor_group_the_founder_already_knows()
    {
        var root = new RootCommand("test");
        WorkerCommands.AddTo(root);
        var conductor = root.Subcommands.Single(c => c.Name == "conductor");

        Assert.Contains(conductor.Subcommands, c => c.Name == "sessions");
        Assert.Contains(conductor.Subcommands, c => c.Name == "unattended");
    }

    [Fact]
    public void A_standing_ceiling_is_sent_as_the_standing_ceiling()
    {
        var request = ParseSessions("conductor", "sessions", "4");

        Assert.Equal(new ConductorSessionsRequest(4, null, null, null, Clear: false), request);
    }

    /// <summary>The number the hub refuses reaches it intact: the refusal is the hub's to word, not the CLI's.</summary>
    [Fact]
    public void A_number_the_hub_will_refuse_is_still_sent_rather_than_second_guessed()
    {
        var request = ParseSessions("conductor", "sessions", "0");

        Assert.Equal(0, request.Sessions);
        Assert.False(request.Clear);
    }

    [Fact]
    public void A_window_puts_its_own_sessions_in_the_window_field_and_not_the_standing_one()
    {
        var request = ParseUnattended("conductor", "unattended", "--from", "22:00", "--to", "07:00", "--sessions", "1");

        Assert.Equal(new ConductorSessionsRequest(null, "22:00", "07:00", 1, Clear: false), request);
    }

    [Fact]
    public void Half_a_window_is_sent_as_half_a_window_so_the_hub_says_what_is_missing()
    {
        var request = ParseUnattended("conductor", "unattended", "--from", "22:00");

        Assert.Equal(new ConductorSessionsRequest(null, "22:00", null, null, Clear: false), request);
    }

    [Fact]
    public void Clear_is_a_clear_and_carries_no_numbers_with_it()
    {
        var request = ParseUnattended("conductor", "unattended", "--clear");

        Assert.Equal(new ConductorSessionsRequest(null, null, null, null, Clear: true), request);
    }

    /// <summary>
    /// The CLI is AOT-compiled, so the request has to travel on the source-generated context. A type missing from
    /// <see cref="MuthurJsonContext"/> builds perfectly and throws the moment the founder runs the command.
    /// </summary>
    [Fact]
    public void The_request_travels_on_the_source_generated_context()
    {
        var json = JsonSerializer.Serialize(
            new ConductorSessionsRequest(null, "22:00", "07:00", 1, Clear: false), MuthurJsonContext.Default.ConductorSessionsRequest);

        Assert.Equal("""{"unattendedFrom":"22:00","unattendedTo":"07:00","unattendedSessions":1,"clear":false}""", json);
    }
}
