using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Muthur.Contracts;
using Muthur.Server.Auth;
using Muthur.Server.Services;
using ConsolePage = Muthur.Server.Components.Pages.Console;

namespace Muthur.Server.Tests;

/// <summary>
/// The console's Roles and Projects controls. T-24 is <c>attended</c>, so no test here presses a button: every
/// control on this dashboard rides a SignalR circuit. What a machine checks is everything short of the click —
/// that both sections and their labels are in the fetched HTML, that the rule the validator checkbox applies is
/// right, and that the services behind the two buttons do what the buttons claim.
/// </summary>
public sealed class DashboardConsoleRolesTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
    }

    private RoleService Roles => _hub.Services.GetRequiredService<RoleService>();

    private ProjectService ProjectsService => _hub.Services.GetRequiredService<ProjectService>();

    private Task<string> PageAsync() => _hub.CreateClient().GetStringAsync("/console");

    // -- Roles ---------------------------------------------------------------------------------------------

    /// <summary>The Roles control, whole: every input the founder fills, labelled, and the button.</summary>
    [Fact]
    public async Task The_page_renders_the_roles_control_with_every_label_and_its_button()
    {
        var response = await _hub.CreateClient().GetAsync("/console");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("An unhandled error", page);

        Assert.Contains("class=\"panel-title\">Roles<", page);
        Assert.Matches("<label for=\"role-key\">Key</label>", page);
        Assert.Matches("<label for=\"role-brief\">Brief file</label>", page);
        Assert.Matches("<button class=\"btn btn-go\"[^>]*>Define</button>", page);

        // The checkbox stays on the page whatever the key says, because a founder who cannot see what was
        // inferred cannot disagree with it. Its label is what it does, not what the flag is called.
        Assert.Matches("<label for=\"role-validator\">Gives validation verdicts</label>", page);
        Assert.Matches("<input type=\"checkbox\" id=\"role-validator\"", page);

        // Briefs are files. A textarea here would quietly become the source of truth for a versioned document.
        Assert.DoesNotContain("<textarea", page, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The trap, closed, as a plain function of the key. A bare <c>validator</c> is the case the hub gets wrong:
    /// it reads the <c>-validator</c> suffix and nothing else, so the one key a founder is most likely to type
    /// for their first validator is the one key that would not have been one.
    /// </summary>
    [Theory]
    [InlineData("validator", true)]
    [InlineData("win-validator", true)]
    [InlineData("release-validator", true)]
    // Typed, so it arrives with whatever case and spacing the founder used; the hub lower-cases and trims the
    // key before storing it, and an inference that did not would show the box off for a role that becomes one.
    [InlineData("Validator", true)]
    [InlineData("  win-validator  ", true)]
    [InlineData("WIN-Validator", true)]
    // Near misses, which are ordinary roles and must stay ordinary: only the whole word, only at the end.
    [InlineData("validators", false)]
    [InlineData("validator-lite", false)]
    [InlineData("winvalidator", false)]
    [InlineData("comms-oncall", false)]
    [InlineData("", false)]
    public void A_key_that_names_a_validator_infers_the_flag(string key, bool expected) =>
        Assert.Equal(expected, ConsolePage.InfersValidator(key));

    /// <summary>
    /// Why the checkbox is inferred rather than defaulted off, proved against the service it is inferring for:
    /// send no flag and the hub reads the suffix, so <c>win-validator</c> may give verdicts and <c>validator</c>
    /// may not. The console sends the flag every time, which is the whole of the fix.
    /// </summary>
    [Fact]
    public async Task The_hub_alone_leaves_a_bare_validator_role_unable_to_give_verdicts()
    {
        var suffixed = await Roles.DefineAsync(Caller.Founder, new DefineRoleRequest("win-validator", "# win\n"));
        Assert.True(suffixed.IsValidator);

        // The trap: the same intention, a key the founder finds more natural, and a role that gates nothing.
        var bare = await Roles.DefineAsync(Caller.Founder, new DefineRoleRequest("validator", "# it\n"));
        Assert.False(bare.IsValidator);

        // What the console sends instead, for that same key, because InfersValidator answered true for it.
        Assert.True(ConsolePage.InfersValidator("validator"));
        var closed = await Roles.DefineAsync(
            Caller.Founder, new DefineRoleRequest("validator", "# it\n", ConsolePage.InfersValidator("validator")));
        Assert.True(closed.IsValidator);
    }

    // -- Projects ------------------------------------------------------------------------------------------

    /// <summary>A row per project: what its gate is now, and the two controls that change it.</summary>
    [Fact]
    public async Task The_page_renders_a_row_per_project_with_its_gate_and_the_controls_that_change_it()
    {
        await _hub.AddProjectAsync("scratch", _repo.Path, LandMode.Pr, validators: ["win-validator", "docs-validator"]);

        var page = await PageAsync();

        Assert.Contains("class=\"panel-title\">Projects<", page);
        Assert.Contains("<span class=\"agent-name\">scratch</span>", page);
        Assert.Contains("land: pr", page);
        Assert.Contains("<span class=\"pill pill-role\">win-validator</span>", page);
        Assert.Contains("<span class=\"pill pill-role\">docs-validator</span>", page);

        Assert.Matches("<label for=\"project-validators-scratch\">Validators</label>", page);
        Assert.Matches("<input id=\"project-validators-scratch\"[^>]*value=\"win-validator, docs-validator\"", page);
        Assert.Matches("<label for=\"project-land-scratch\">Land mode</label>", page);
        Assert.Matches("<button class=\"btn btn-go\"[^>]*>Save</button>", page);

        // Land mode is a closed set of MUTHUR's own two words, so it is chosen rather than typed — and the
        // option standing is the one the project is on, not whichever the markup happens to list first.
        Assert.Contains("<option value=\"pr\" selected>pr</option>", page, StringComparison.Ordinal);
        Assert.Contains("<option value=\"merge\">merge</option>", page, StringComparison.Ordinal);

        // The board is the list of tasks; this page never becomes a second one.
        Assert.DoesNotContain(_repo.Path, page, StringComparison.Ordinal);
    }

    /// <summary>
    /// One state, one sentence. A project nobody validates is a legitimate choice and a dangerous one, and the
    /// console says about it exactly what /projects says — the same words in the same element, taken from that
    /// page rather than retyped here, so the two cannot drift apart without this failing.
    /// </summary>
    [Fact]
    public async Task A_project_with_no_required_validators_says_what_the_projects_page_says()
    {
        await _hub.AddProjectAsync("scratch", _repo.Path);

        var projects = await _hub.CreateClient().GetStringAsync("/projects");
        var sentence = Regex.Match(projects, "<span class=\"panel-sub\">none required[^<]*</span>").Value;
        Assert.Contains("goes straight to validated", sentence, StringComparison.Ordinal);

        var page = await PageAsync();
        Assert.Contains(sentence, page, StringComparison.Ordinal);
        Assert.Contains("<span class=\"pill pill-ungated\">ungated</span>", page);
    }

    /// <summary>
    /// The sentence belongs to the state and not to the page: a project that is gated must not carry it, or it
    /// would mean nothing where it does appear.
    /// </summary>
    [Fact]
    public async Task A_gated_project_carries_neither_the_sentence_nor_the_ungated_pill()
    {
        await _hub.AddProjectAsync("scratch", _repo.Path, validators: ["win-validator"]);

        var page = await PageAsync();

        Assert.DoesNotContain("none required", page, StringComparison.Ordinal);
        Assert.DoesNotContain("pill-ungated", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// What Save sends, driven through the service the button calls. An emptied box is an empty list and never
    /// null: null is how the request says "leave the gate alone", and a console that sent it would answer a
    /// founder who cleared the field with a page that looks unchanged and a gate that still stands.
    /// </summary>
    [Fact]
    public async Task An_emptied_validator_box_ungates_the_project_where_an_absent_one_would_not()
    {
        await _hub.AddProjectAsync("scratch", _repo.Path, validators: ["win-validator"]);

        var untouched = await ProjectsService.UpdateAsync(
            Caller.Founder, "scratch", new UpdateProjectRequest(LandMode: LandMode.Pr));
        Assert.Equal("win-validator", Assert.Single(untouched.RequiredValidators));
        Assert.False(untouched.Ungated);

        var cleared = await ProjectsService.UpdateAsync(
            Caller.Founder, "scratch", new UpdateProjectRequest(LandMode: LandMode.Pr, RequiredValidators: []));
        Assert.Empty(cleared.RequiredValidators);
        Assert.True(cleared.Ungated);

        Assert.Contains("none required", await PageAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A validator name no role could hold is refused by the service, in the service's own words, and the
    /// console renders those rather than wording of its own — so the page and the CLI say one thing about it.
    /// </summary>
    [Fact]
    public async Task A_validator_name_no_role_could_hold_is_refused_in_the_services_own_words()
    {
        await _hub.AddProjectAsync("scratch", _repo.Path);

        var refused = await Assert.ThrowsAsync<Muthur.Core.MuthurException>(() => ProjectsService.UpdateAsync(
            Caller.Founder, "scratch", new UpdateProjectRequest(RequiredValidators: ["Not A Role"])));

        Assert.Equal("invalid_validator", refused.Code);
        Assert.Contains("cannot name a role", refused.Message, StringComparison.Ordinal);
        Assert.Empty((await ProjectsService.GetAsync("scratch")).RequiredValidators);
    }

    /// <summary>
    /// Ingest sources are deliberately not on this row. A source the hub cannot poll fails silently for as long
    /// as nobody looks, which is why <c>muthur doctor</c> probes them; a text box that saved one without
    /// probing would be the quietest way on this page to break a project.
    /// </summary>
    [Fact]
    public async Task The_projects_row_offers_no_ingest_field()
    {
        await _hub.AddProjectAsync("scratch", _repo.Path, ingest: []);

        var page = await PageAsync();

        Assert.DoesNotContain("ingest", page, StringComparison.OrdinalIgnoreCase);
    }
}
