using System.Net;
using System.Net.Http.Json;
using Muthur.Contracts;
using Muthur.Core;

namespace Muthur.Server.Tests;

/// <summary>
/// A project's required validators name roles. A string no role key could ever be gates nothing, so it is
/// refused where it is typed rather than discovered by the conductor at two in the morning.
/// </summary>
public sealed class ProjectValidatorTests : IDisposable
{
    private readonly HubFactory _hub = new();

    public void Dispose() => _hub.Dispose();

    private Task<HttpResponseMessage> AddAsync(params string[] validators) =>
        _hub.Founder().PostAsJsonAsync(Routes.Projects, new AddProjectRequest("demo", _hub.DataDir, RequiredValidators: validators));

    private Task<HttpResponseMessage> SetAsync(params string[] validators) =>
        _hub.Founder().PutAsJsonAsync($"{Routes.Projects}/demo", new UpdateProjectRequest(RequiredValidators: validators));

    private async Task<IReadOnlyList<string>> ValidatorsAsync() =>
        (await _hub.CreateClient().GetFromJsonAsync(Routes.Projects, MuthurJsonContext.Default.IReadOnlyListProjectDto))!
            .Single(p => p.Key == "demo").RequiredValidators;

    public static TheoryData<string> IllegalKeys => new()
    {
        "win.validator",
        "win_validator",
        "win validator",
        "-win-validator",
        "Not A Role!",
        new string('a', RoleKey.MaxLength + 1),
    };

    [Theory]
    [MemberData(nameof(IllegalKeys))]
    public async Task A_validator_no_role_could_ever_be_is_refused_when_a_project_is_added(string key)
    {
        var response = await AddAsync(key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.ReadErrorAsync();
        Assert.Equal("invalid_validator", error.Code);
        Assert.Contains(key.Trim().ToLowerInvariant(), error.Message, StringComparison.Ordinal); // it names the offender

        var projects = await _hub.CreateClient().GetFromJsonAsync(Routes.Projects, MuthurJsonContext.Default.IReadOnlyListProjectDto);
        Assert.Empty(projects!);
    }

    [Theory]
    [MemberData(nameof(IllegalKeys))]
    public async Task And_when_one_is_changed(string key)
    {
        (await AddAsync("win-validator")).EnsureSuccessStatusCode();

        var response = await SetAsync(key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("invalid_validator", (await response.ReadErrorAsync()).Code);
        // The throw happens inside the mutation, so the transaction rolling back is the thing worth pinning.
        Assert.Equal(["win-validator"], await ValidatorsAsync());
    }

    [Fact]
    public async Task A_capital_is_lowercased_rather_than_refused_exactly_as_role_define_does()
    {
        (await AddAsync("Win-Validator")).EnsureSuccessStatusCode();

        Assert.Equal(["win-validator"], await ValidatorsAsync());
    }

    [Fact]
    public async Task A_legal_key_still_works_including_the_longest_one_there_is()
    {
        var longest = new string('a', RoleKey.MaxLength);

        (await AddAsync("win-validator", longest)).EnsureSuccessStatusCode();

        Assert.Equal(["win-validator", longest], await ValidatorsAsync());
    }

    [Fact]
    public async Task A_role_that_does_not_exist_yet_is_still_accepted_and_doctor_is_what_says_so()
    {
        (await AddAsync("win-validator")).EnsureSuccessStatusCode();

        var checks = (await _hub.CreateClient().GetFromJsonAsync($"{Routes.Doctor}?probe=false", MuthurJsonContext.Default.DoctorDto))!.Checks;
        var gate = checks.Single(c => c.Category == "project" && c.Subject == "demo" && c.Detail.Contains("Requires validator", StringComparison.Ordinal));
        Assert.Equal(CheckStatus.Fail, gate.Status);
        Assert.Equal("Requires validator 'win-validator', which is not a defined role. muthur role define win-validator --founder", gate.Detail);
    }

    [Fact]
    public async Task Role_define_is_unchanged_it_just_reads_the_rule_from_somewhere_else()
    {
        var response = await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win.validator"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.ReadErrorAsync();
        Assert.Equal("invalid_key", error.Code);
        Assert.Equal($"Role keys are {RoleKey.Rule}.", error.Message);
    }
}
