using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>Two validators may hold one role; the claim is what stops them spending two sessions on one task.</summary>
public sealed class ValidationClaimTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    /// <summary>A project requiring these validator roles, each with room for two holders.</summary>
    private async Task SetUpAsync(params string[] validators)
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: validators);
        foreach (var key in validators)
            (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest(key, $"Brief for {key}", Holders: 2))).EnsureSuccessStatusCode();
    }

    private async Task<HttpClient> ValidatorAsync(string name, params string[] roles)
    {
        var client = await _hub.RegisterAgentAsync(name);
        foreach (var role in roles)
            (await client.PostAsync(Routes.RoleAction(role, "take"), null)).EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>A task of this owner's, built on its own branch and sitting in 'validating'.</summary>
    private async Task<string> ValidatingTaskAsync(HttpClient owner, string title, string file)
    {
        var task = await owner.AddTaskAsync(title);
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(task.Id, "spec", new SetSpecRequest($"specs/{task.Id}.md"))).EnsureSuccessStatusCode();
        var branch = $"task/{task.Id}-work";
        _repo.BranchWithFile(branch, file, $"{file}\n");
        (await owner.PostActionAsync(task.Id, "implemented", new ImplementedRequest(branch))).EnsureSuccessStatusCode();
        return task.Id;
    }

    private static Task<HttpResponseMessage> ClaimAsync(HttpClient client, string id, string role = "win-validator") =>
        client.PostActionAsync(id, "validate-claim", new ClaimValidationRequest(role));

    private static Task<HttpResponseMessage> GiveBackAsync(HttpClient client, string id, string role = "win-validator") =>
        client.PostActionAsync(id, "validate-release", new ClaimValidationRequest(role));

    private static async Task<IReadOnlyList<TaskDto>> PendingAsync(HttpClient client, string? role = "win-validator", bool all = false)
    {
        var query = new List<string>();
        if (role is not null) query.Add($"role={role}");
        if (all) query.Add("all=true");
        var url = query.Count == 0 ? Routes.Validations : $"{Routes.Validations}?{string.Join('&', query)}";
        return (await client.GetFromJsonAsync(url, MuthurJsonContext.Default.IReadOnlyListTaskDto))!;
    }

    private Task<int> SweepAsync() =>
        _hub.Services.GetRequiredService<LifecycleService>().SweepExpiredValidationClaimsAsync();

    private async Task<ValidationDto> RowAsync(string id, string role = "win-validator") =>
        (await _hub.Founder().GetTaskAsync(id)).Task.Validations.Single(v => v.Validator == role);

    [Fact]
    public async Task A_claimed_task_is_refused_to_a_second_validator_claiming_or_giving_a_verdict()
    {
        await SetUpAsync("win-validator");
        var owner = await _hub.RegisterAgentAsync("owner");
        var one = await ValidatorAsync("one", "win-validator");
        var two = await ValidatorAsync("two", "win-validator");
        var id = await ValidatingTaskAsync(owner, "Build the feature", "feature.txt");

        var claimed = await (await ClaimAsync(one, id)).ReadTaskAsync();
        Assert.Equal("one", claimed.Validations.Single().ClaimedBy);
        Assert.NotNull(claimed.Validations.Single().ClaimExpires);

        (await ClaimAsync(one, id)).EnsureSuccessStatusCode();   // claiming one you hold just renews it

        var second = await ClaimAsync(two, id);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var error = await second.ReadErrorAsync();
        Assert.Equal("validation_claimed", error.Code);
        Assert.Contains("by 'one'", error.Message);

        // A verdict is a session already spent, so it is refused the same way rather than overwriting the claim.
        var verdict = await two.PostActionAsync(id, "pass", new VerdictRequest("win-validator", "I did not run this"));
        Assert.Equal(HttpStatusCode.Conflict, verdict.StatusCode);
        Assert.Equal("validation_claimed", (await verdict.ReadErrorAsync()).Code);

        var passed = await (await one.PostActionAsync(id, "pass", new VerdictRequest("win-validator", "ran the app, works"))).ReadTaskAsync();
        Assert.Equal(TaskState.Validated, passed.State);
        Assert.Null(passed.Validations.Single().ClaimedBy);     // decided: nobody is working it
        Assert.Null(passed.Validations.Single().ClaimExpires);

        var events = await _hub.CreateClient().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto);
        Assert.Single(events!, e => e.Type == "validation.claimed");   // the renewal was not a second claim
    }

    [Fact]
    public async Task A_lone_validator_still_gives_a_verdict_in_one_step_and_leaves_no_claim_behind()
    {
        await SetUpAsync("win-validator");
        var owner = await _hub.RegisterAgentAsync("owner");
        var one = await ValidatorAsync("one", "win-validator");
        var id = await ValidatingTaskAsync(owner, "Build the feature", "feature.txt");

        var passed = await (await one.PostActionAsync(id, "pass", new VerdictRequest("win-validator", "ran the app, works"))).ReadTaskAsync();

        Assert.Equal(TaskState.Validated, passed.State);
        var row = passed.Validations.Single();
        Assert.Equal("yes", row.Verdict);
        Assert.Null(row.ClaimedBy);
        Assert.Null(row.ClaimExpires);
        Assert.Equal(0, await SweepAsync());   // the implicit claim left nothing behind for the sweep to find
    }

    [Fact]
    public async Task Validate_list_hides_what_another_validator_has_claimed_unless_you_ask_for_all()
    {
        await SetUpAsync("win-validator");
        var owner = await _hub.RegisterAgentAsync("owner");
        var one = await ValidatorAsync("one", "win-validator");
        var two = await ValidatorAsync("two", "win-validator");
        var first = await ValidatingTaskAsync(owner, "First", "first.txt");
        var second = await ValidatingTaskAsync(owner, "Second", "second.txt");

        (await ClaimAsync(one, first)).EnsureSuccessStatusCode();

        Assert.Equal([second], (await PendingAsync(two)).Select(t => t.Id));
        Assert.Equal([first, second], (await PendingAsync(two, all: true)).Select(t => t.Id));
        // The claimer still sees what it took: a list that hid your own claim would read as the work vanishing.
        Assert.Equal([first, second], (await PendingAsync(one)).Select(t => t.Id));
        // Without a role there is no single row per task to read a claim from, so nothing is filtered.
        Assert.Equal([first, second], (await PendingAsync(two, role: null)).Select(t => t.Id));
    }

    [Fact]
    public async Task A_claim_lapses_when_its_validator_goes_quiet_and_the_sweep_hands_the_pair_back()
    {
        await SetUpAsync("win-validator");
        var owner = await _hub.RegisterAgentAsync("owner");
        var one = await ValidatorAsync("one", "win-validator");
        var two = await ValidatorAsync("two", "win-validator");
        var id = await ValidatingTaskAsync(owner, "Build the feature", "feature.txt");
        (await ClaimAsync(one, id)).EnsureSuccessStatusCode();

        Assert.Equal(0, await SweepAsync());

        // 'one' says nothing for longer than its lease while 'two' keeps reporting in.
        for (var i = 0; i < 3; i++)
        {
            _hub.Clock.Advance(TimeSpan.FromMinutes(11));
            (await two.PostAsJsonAsync(Routes.AgentHeartbeat, new HeartbeatRequest())).EnsureSuccessStatusCode();
        }

        Assert.Equal(1, await SweepAsync());
        Assert.Null((await RowAsync(id)).ClaimedBy);

        var events = await _hub.CreateClient().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto);
        Assert.Contains(events!, e => e.Type == "validation.claim_expired"
            && e.Payload.GetProperty("validator").GetString() == "win-validator"
            && e.Payload.GetProperty("agent").GetString() == "one");

        (await ClaimAsync(two, id)).EnsureSuccessStatusCode();   // the pair is genuinely open again
        Assert.Equal("two", (await RowAsync(id)).ClaimedBy);
    }

    [Fact]
    public async Task Signs_of_life_keep_a_validation_claim()
    {
        await SetUpAsync("win-validator");
        var owner = await _hub.RegisterAgentAsync("owner");
        var one = await ValidatorAsync("one", "win-validator");
        var id = await ValidatingTaskAsync(owner, "Build the feature", "feature.txt");
        (await ClaimAsync(one, id)).EnsureSuccessStatusCode();

        for (var i = 0; i < 3; i++)
        {
            _hub.Clock.Advance(TimeSpan.FromMinutes(20));
            (await one.PostAsJsonAsync(Routes.AgentHeartbeat, new HeartbeatRequest())).EnsureSuccessStatusCode();
        }

        Assert.Equal(0, await SweepAsync());
        Assert.Equal("one", (await RowAsync(id)).ClaimedBy);
    }

    [Fact]
    public async Task A_failed_verdict_ends_the_round_and_clears_every_claim_on_the_task()
    {
        await SetUpAsync("win-validator", "web-validator");
        var owner = await _hub.RegisterAgentAsync("owner");
        var one = await ValidatorAsync("one", "win-validator");
        var two = await ValidatorAsync("two", "web-validator");
        var id = await ValidatingTaskAsync(owner, "Build the feature", "feature.txt");
        (await ClaimAsync(one, id)).EnsureSuccessStatusCode();
        (await ClaimAsync(two, id, "web-validator")).EnsureSuccessStatusCode();

        var failed = await (await one.PostActionAsync(id, "fail", new VerdictRequest("win-validator", "crashes on launch: repro steps…"))).ReadTaskAsync();

        Assert.Equal(TaskState.InProgress, failed.State);
        Assert.Equal(2, failed.Validations.Count);
        Assert.All(failed.Validations, v => Assert.Null(v.ClaimedBy));
        Assert.All(failed.Validations, v => Assert.Null(v.ClaimExpires));
        // …and in the table, not only in the answer the verdict came back with.
        var stored = (await _hub.Founder().GetTaskAsync(id)).Task.Validations;
        Assert.All(stored, v => Assert.Null(v.ClaimedBy));
        Assert.Equal(0, await SweepAsync());
    }

    [Fact]
    public async Task Claiming_a_task_you_own_or_a_role_you_do_not_hold_is_refused_before_a_session_is_spent()
    {
        await SetUpAsync("win-validator");
        var owner = await ValidatorAsync("owner", "win-validator");
        var id = await ValidatingTaskAsync(owner, "Build the feature", "feature.txt");

        var self = await ClaimAsync(owner, id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);
        Assert.Equal("self_validation", (await self.ReadErrorAsync()).Code);

        var stranger = await _hub.RegisterAgentAsync("stranger");
        var noRole = await ClaimAsync(stranger, id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noRole.StatusCode);
        Assert.Equal("role_not_held", (await noRole.ReadErrorAsync()).Code);
    }

    [Fact]
    public async Task A_pair_that_already_has_its_verdict_cannot_be_claimed()
    {
        await SetUpAsync("win-validator", "web-validator");
        var owner = await _hub.RegisterAgentAsync("owner");
        var one = await ValidatorAsync("one", "win-validator");
        var two = await ValidatorAsync("two", "win-validator", "web-validator");
        var id = await ValidatingTaskAsync(owner, "Build the feature", "feature.txt");
        (await one.PostActionAsync(id, "pass", new VerdictRequest("win-validator", "ran the app, works"))).EnsureSuccessStatusCode();

        var late = await ClaimAsync(two, id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, late.StatusCode);
        Assert.Equal("already_decided", (await late.ReadErrorAsync()).Code);

        (await ClaimAsync(two, id, "web-validator")).EnsureSuccessStatusCode();   // the undecided pair is still open
    }

    [Fact]
    public async Task A_claim_is_given_back_by_its_claimer_or_the_founder_and_by_nobody_else()
    {
        await SetUpAsync("win-validator");
        var owner = await _hub.RegisterAgentAsync("owner");
        var one = await ValidatorAsync("one", "win-validator");
        var two = await ValidatorAsync("two", "win-validator");
        var id = await ValidatingTaskAsync(owner, "Build the feature", "feature.txt");

        var nothing = await GiveBackAsync(two, id);
        Assert.Equal("not_claimer", (await nothing.ReadErrorAsync()).Code);

        (await ClaimAsync(one, id)).EnsureSuccessStatusCode();
        var theirs = await GiveBackAsync(two, id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, theirs.StatusCode);
        Assert.Equal("not_claimer", (await theirs.ReadErrorAsync()).Code);

        var given = await (await GiveBackAsync(one, id)).ReadTaskAsync();
        Assert.Null(given.Validations.Single().ClaimedBy);

        (await ClaimAsync(two, id)).EnsureSuccessStatusCode();
        (await GiveBackAsync(_hub.Founder(), id)).EnsureSuccessStatusCode();
        Assert.Null((await RowAsync(id)).ClaimedBy);

        var events = await _hub.CreateClient().GetFromJsonAsync(Routes.Events, MuthurJsonContext.Default.IReadOnlyListEventDto);
        Assert.Equal(2, events!.Count(e => e.Type == "validation.claimed"));
        Assert.Equal(2, events!.Count(e => e.Type == "validation.released"));
    }

    [Fact]
    public async Task The_queue_counts_what_is_waiting_per_role_and_names_the_oldest_wait()
    {
        await SetUpAsync("win-validator");
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("web-validator", "nothing waits on this"))).EnsureSuccessStatusCode();
        var owner = await _hub.RegisterAgentAsync("owner");
        var one = await ValidatorAsync("one", "win-validator");
        var first = await ValidatingTaskAsync(owner, "First", "first.txt");
        var oldest = _hub.Clock.GetUtcNow();
        _hub.Clock.Advance(TimeSpan.FromMinutes(5));
        await ValidatingTaskAsync(owner, "Second", "second.txt");
        (await ClaimAsync(one, first)).EnsureSuccessStatusCode();

        var queue = await one.GetFromJsonAsync(Routes.ValidationQueue, MuthurJsonContext.Default.IReadOnlyListValidationQueueDto);

        Assert.Equal(2, queue!.Count);
        var win = queue[0];
        Assert.Equal("win-validator", win.Role);
        Assert.Equal(2, win.Capacity);
        Assert.Equal(1, win.Holders);
        Assert.Equal(2, win.Waiting);
        Assert.Equal(1, win.Claimed);
        Assert.Equal(oldest, win.OldestWaitingSince);

        // A role nobody holds and nothing waits on still appears: that it is idle is the fact worth showing.
        var web = queue[1];
        Assert.Equal("web-validator", web.Role);
        Assert.Equal(0, web.Holders);
        Assert.Equal(0, web.Waiting);
        Assert.Null(web.OldestWaitingSince);
    }
}
