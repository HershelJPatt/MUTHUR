using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

public sealed class RoleTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    private async Task<IReadOnlyList<RoleDto>> RolesAsync() =>
        (await _hub.CreateClient().GetFromJsonAsync(Routes.Roles, MuthurJsonContext.Default.IReadOnlyListRoleDto))!;

    private async Task<RoleDto> RoleAsync(string key) => (await RolesAsync()).Single(r => r.Key == key);

    private static IEnumerable<string> Names(RoleDto role) => role.Holders.Select(h => h.Agent);

    [Fact]
    public async Task Only_the_founder_writes_briefs_and_agents_read_them()
    {
        var agent = await _hub.RegisterAgentAsync("reader");
        var denied = await agent.PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("comms-oncall", "do whatever"));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", "# Windows validator\nBuild, launch, drive."))).EnsureSuccessStatusCode();

        var brief = await agent.GetFromJsonAsync(Routes.RoleAction("win-validator", "brief"), MuthurJsonContext.Default.RoleBriefDto);
        Assert.Contains("Build, launch, drive.", brief!.Brief);
        Assert.True(brief.IsValidator, "keys ending in -validator default to validator roles");
    }

    [Fact]
    public async Task A_role_has_one_holder_and_the_hold_is_a_lease()
    {
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("comms-oncall"))).EnsureSuccessStatusCode();
        var first = await _hub.RegisterAgentAsync("first");
        var second = await _hub.RegisterAgentAsync("second");
        Assert.Equal(1, (await RoleAsync("comms-oncall")).Capacity);

        (await first.PostAsync(Routes.RoleAction("comms-oncall", "take"), null)).EnsureSuccessStatusCode();
        var contested = await second.PostAsync(Routes.RoleAction("comms-oncall", "take"), null);
        Assert.Equal(HttpStatusCode.Conflict, contested.StatusCode);
        Assert.Equal("role_held", (await contested.ReadErrorAsync()).Code);

        // Signs of life keep the hold…
        for (var i = 0; i < 3; i++)
        {
            _hub.Clock.Advance(TimeSpan.FromMinutes(20));
            (await first.PostAsJsonAsync(Routes.AgentHeartbeat, new HeartbeatRequest())).EnsureSuccessStatusCode();
        }
        Assert.Equal(["first"], Names(await RoleAsync("comms-oncall")));

        // …silence loses it.
        _hub.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Empty((await RoleAsync("comms-oncall")).Holders);
        (await second.PostAsync(Routes.RoleAction("comms-oncall", "take"), null)).EnsureSuccessStatusCode();
        Assert.Equal(["second"], Names(await RoleAsync("comms-oncall")));

        var agents = await _hub.CreateClient().GetFromJsonAsync(Routes.Agents, MuthurJsonContext.Default.IReadOnlyListAgentDto);
        Assert.Equal(["comms-oncall"], agents!.Single(a => a.Name == "second").Roles);
        Assert.Empty(agents!.Single(a => a.Name == "first").Roles);
    }

    [Fact]
    public async Task A_validator_role_admits_as_many_holders_as_its_capacity()
    {
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", Holders: 2))).EnsureSuccessStatusCode();
        var one = await _hub.RegisterAgentAsync("one");
        var two = await _hub.RegisterAgentAsync("two");
        var three = await _hub.RegisterAgentAsync("three");

        (await one.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        _hub.Clock.Advance(TimeSpan.FromSeconds(1)); // so the holders have distinct acquisition times
        (await two.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();

        var role = await RoleAsync("win-validator");
        Assert.Equal(2, role.Capacity);
        Assert.Equal(["one", "two"], Names(role));

        var full = await three.PostAsync(Routes.RoleAction("win-validator", "take"), null);
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        var error = await full.ReadErrorAsync();
        Assert.Equal("role_held", error.Code);
        Assert.Contains("2 of 2 held by one, two", error.Message);

        // Taking a role you already hold is still a renewal, not a second seat.
        (await one.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        Assert.Equal(["one", "two"], Names(await RoleAsync("win-validator")));
    }

    [Fact]
    public async Task Only_a_validator_role_may_have_more_than_one_holder()
    {
        var post = await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("comms-oncall", Holders: 2));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
        Assert.Equal("single_holder", (await post.ReadErrorAsync()).Code);
        Assert.Empty(await RolesAsync()); // refused outright: the role was not created

        // The same call becomes legal the moment the role is a validator role.
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("platform-checks", IsValidator: true, Holders: 3))).EnsureSuccessStatusCode();
        Assert.Equal(3, (await RoleAsync("platform-checks")).Capacity);
    }

    /// <summary>
    /// The hole a validator found in the first round of this task: the capacity gate only ran when --holders
    /// was passed, so converting a two-holder validator into a standing post left a standing post two agents
    /// could hold. The invariant belongs to the role's state, not to the argument that last touched it.
    /// </summary>
    [Fact]
    public async Task Converting_a_validator_role_into_a_standing_post_cannot_leave_its_capacity_behind()
    {
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("platform-checks", IsValidator: true, Holders: 2))).EnsureSuccessStatusCode();

        var converted = await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("platform-checks", IsValidator: false));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, converted.StatusCode);
        Assert.Equal("single_holder", (await converted.ReadErrorAsync()).Code);

        // Refused outright, so the role is exactly as it was: still a validator, still at two.
        var unchanged = await RoleAsync("platform-checks");
        Assert.True(unchanged.IsValidator);
        Assert.Equal(2, unchanged.Capacity);

        // Two agents could hold it before, and that is precisely what must not survive the conversion.
        var one = await _hub.RegisterAgentAsync("one");
        var two = await _hub.RegisterAgentAsync("two");
        (await one.PostAsync(Routes.RoleAction("platform-checks", "take"), null)).EnsureSuccessStatusCode();
        _hub.Clock.Advance(TimeSpan.FromSeconds(1));
        (await two.PostAsync(Routes.RoleAction("platform-checks", "take"), null)).EnsureSuccessStatusCode();

        // Saying both things in one call is how the founder actually converts it, and that is accepted.
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("platform-checks", IsValidator: false, Holders: 1))).EnsureSuccessStatusCode();
        var post = await RoleAsync("platform-checks");
        Assert.False(post.IsValidator);
        Assert.Equal(1, post.Capacity);
        Assert.Equal(["one", "two"], Names(post)); // standing holds are still not evicted by a lower ceiling
    }

    /// <summary>
    /// Also found in that round: the define response reported an empty holder list while two agents held the
    /// role, so the answer to "what did I just do" disagreed with the very next take.
    /// </summary>
    [Fact]
    public async Task Defining_a_role_answers_with_who_holds_it_now_not_with_an_empty_list()
    {
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", Holders: 2))).EnsureSuccessStatusCode();
        var one = await _hub.RegisterAgentAsync("one");
        var two = await _hub.RegisterAgentAsync("two");
        (await one.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        _hub.Clock.Advance(TimeSpan.FromSeconds(1));
        (await two.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();

        var lowered = await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", Holders: 1));
        lowered.EnsureSuccessStatusCode();
        var answered = (await lowered.Content.ReadFromJsonAsync(MuthurJsonContext.Default.RoleDto))!;

        Assert.Equal(1, answered.Capacity);
        Assert.Equal(["one", "two"], Names(answered));
        Assert.Equal(Names(await RoleAsync("win-validator")), Names(answered)); // and it agrees with the listing

        // A lease that has lapsed is not a holder: the response reads live occupancy, not every row.
        _hub.Clock.Advance(TimeSpan.FromDays(2));
        var later = await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", Brief: "# win-validator\nDrive it."));
        later.EnsureSuccessStatusCode();
        Assert.Empty((await later.Content.ReadFromJsonAsync(MuthurJsonContext.Default.RoleDto))!.Holders);
    }

    [Fact]
    public async Task A_role_needs_at_least_one_holder()
    {
        var none = await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", Holders: 0));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        Assert.Equal("invalid_holders", (await none.ReadErrorAsync()).Code);
    }

    [Fact]
    public async Task Lowering_the_capacity_evicts_nobody()
    {
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", Holders: 2))).EnsureSuccessStatusCode();
        var one = await _hub.RegisterAgentAsync("one");
        var two = await _hub.RegisterAgentAsync("two");
        (await one.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        _hub.Clock.Advance(TimeSpan.FromSeconds(1));
        (await two.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();

        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", Holders: 1))).EnsureSuccessStatusCode();

        var role = await RoleAsync("win-validator");
        Assert.Equal(1, role.Capacity);
        Assert.Equal(["one", "two"], Names(role)); // the new ceiling applies to the next take, not to standing holds

        // …and it does apply to the next take, which is told about both leases in its way, not just the oldest.
        var three = await _hub.RegisterAgentAsync("three");
        var full = await three.PostAsync(Routes.RoleAction("win-validator", "take"), null);
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        var error = await full.ReadErrorAsync();
        Assert.Equal("role_held", error.Code);
        Assert.Contains("2 of 1 held by one, two", error.Message);

        var events = await _hub.CreateClient().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto);
        Assert.Contains(events!, e => e.Type == "role.updated" && e.Payload.GetProperty("holders").GetInt32() == 1);
    }

    [Fact]
    public async Task Sweep_frees_lapsed_holds_and_records_it()
    {
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("observability-oncall"))).EnsureSuccessStatusCode();
        var agent = await _hub.RegisterAgentAsync("sleepy");
        (await agent.PostAsync(Routes.RoleAction("observability-oncall", "take"), null)).EnsureSuccessStatusCode();
        var roles = _hub.Services.GetRequiredService<RoleService>();

        Assert.Equal(0, await roles.SweepExpiredHoldsAsync());
        _hub.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, await roles.SweepExpiredHoldsAsync());

        var events = await _hub.CreateClient().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto);
        Assert.Contains(events!, e => e.Type == "role.expired" && e.Payload.GetProperty("agent").GetString() == "sleepy");
    }

    [Fact]
    public async Task Only_the_holder_or_founder_releases()
    {
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("release-oncall"))).EnsureSuccessStatusCode();
        var holder = await _hub.RegisterAgentAsync("holder");
        var other = await _hub.RegisterAgentAsync("other");
        (await holder.PostAsync(Routes.RoleAction("release-oncall", "take"), null)).EnsureSuccessStatusCode();

        var denied = await other.PostAsync(Routes.RoleAction("release-oncall", "release"), null);
        Assert.Equal("not_holder", (await denied.ReadErrorAsync()).Code);

        var released = await holder.PostAsync(Routes.RoleAction("release-oncall", "release"), null);
        Assert.Empty((await released.Content.ReadFromJsonAsync(MuthurJsonContext.Default.RoleDto))!.Holders); // an acknowledgement, like every other command
        Assert.Empty((await RoleAsync("release-oncall")).Holders);
    }

    [Fact]
    public async Task A_holder_releases_only_their_own_hold_and_the_founder_clears_them_all()
    {
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", Holders: 3))).EnsureSuccessStatusCode();
        var one = await _hub.RegisterAgentAsync("one");
        var two = await _hub.RegisterAgentAsync("two");
        (await one.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        _hub.Clock.Advance(TimeSpan.FromSeconds(1));
        (await two.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();

        (await one.PostAsync(Routes.RoleAction("win-validator", "release"), null)).EnsureSuccessStatusCode();
        Assert.Equal(["two"], Names(await RoleAsync("win-validator")));

        _hub.Clock.Advance(TimeSpan.FromSeconds(1));
        (await one.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        Assert.Equal(["two", "one"], Names(await RoleAsync("win-validator")));

        (await _hub.Founder().PostAsync(Routes.RoleAction("win-validator", "release"), null)).EnsureSuccessStatusCode();
        Assert.Empty((await RoleAsync("win-validator")).Holders);

        var events = await _hub.CreateClient().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto);
        Assert.Equal(3, events!.Count(e => e.Type == "role.released"));
    }
}
