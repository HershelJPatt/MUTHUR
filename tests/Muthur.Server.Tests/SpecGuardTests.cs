using System.Net;
using System.Net.Http.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

/// <summary>
/// `task spec` attaches the document implementers are sent to build from. Ledger ids get reused, so the path
/// has to be checked: a spec that is missing, outside the checkout, or written for another task is refused.
/// </summary>
public sealed class SpecGuardTests : IDisposable
{
    private readonly HubFactory _hub = new();
    private readonly TestRepo _repo = new();
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "muthur-tests", "outside-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        _hub.Dispose();
        _repo.Dispose();
        try { Directory.Delete(_outside, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A claimed task — always T-1, the first in an empty ledger — over a real checkout.</summary>
    private async Task<(HttpClient Owner, string TaskId)> ClaimedTaskAsync()
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path);
        var owner = await _hub.RegisterAgentAsync("owner");
        var task = await owner.AddTaskAsync("Build the feature");
        (await owner.ClaimAsync(task.Id)).EnsureSuccessStatusCode();
        return (owner, task.Id);
    }

    private static async Task<ErrorResponse> RefusedAsync(HttpClient owner, string taskId, string path, string? branch = null)
    {
        var response = await owner.PostActionAsync(taskId, "spec", new SetSpecRequest(path, branch));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return await response.ReadErrorAsync();
    }

    /// <summary>
    /// T-50: the ordinary case, which used to be the broken one. Every orchestrator commits its spec on a task
    /// branch and works in a worktree, so the working tree of the shared checkout — the one place this used to
    /// look — is the one place the spec reliably is not.
    /// </summary>
    [Fact]
    public async Task A_spec_committed_only_on_a_task_branch_is_attached()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.BranchWithFile("task/T-1-feature", "specs/T-1-feature.md", "# T-1 — Build the feature\n\nFrozen spec.\n" + TestRepo.Verification);
        Assert.False(File.Exists(Path.Combine(_repo.Path, "specs/T-1-feature.md")));   // genuinely not in the checkout

        var task = await (await owner.PostActionAsync(id, "spec",
            new SetSpecRequest("specs/T-1-feature.md", "task/T-1-feature"))).ReadTaskAsync();

        Assert.Equal("specs/T-1-feature.md", task.SpecPath);
    }

    private async Task<(HttpClient Owner, string TaskId)> BlockedTaskWithBranchAsync(string content)
    {
        await _hub.AddProjectAsync(repoPath: _repo.Path, validators: ["win-validator"]);
        (await _hub.Founder().PutAsJsonAsync(Routes.Roles, new DefineRoleRequest("win-validator", "# win-validator\nDrive it."))).EnsureSuccessStatusCode();
        var owner = await _hub.RegisterAgentAsync("owner");
        var id = (await owner.AddTaskAsync("Build the feature")).Id;
        (await owner.ClaimAsync(id)).EnsureSuccessStatusCode();

        _repo.BranchWithFile("task/T-1-feature", "specs/T-1-feature.md", content);
        _repo.Write("specs/T-1-placeholder.md", "# T-1 — placeholder\n" + TestRepo.Verification);
        _repo.Commit("a spec on main, so the first attach has something to find");
        _repo.Git("checkout", "-q", "task/T-1-feature");
        _repo.Write("specs/T-1-placeholder.md", "# T-1 — placeholder\n" + TestRepo.Verification);
        _repo.Commit("the attached frozen spec also belongs to the implementation");
        _repo.Git("checkout", "-q", "main");

        // The branch reaches the task the way it really does: through implemented, and back on a blocked
        // verdict — which is the shape that left the next holder unable to re-attach.
        (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/T-1-placeholder.md"))).EnsureSuccessStatusCode();
        (await owner.PostActionAsync(id, "implemented", new ImplementedRequest("task/T-1-feature"))).EnsureSuccessStatusCode();
        var validator = await _hub.RegisterAgentAsync("validator");
        (await validator.PostAsync(Routes.RoleAction("win-validator", "take"), null)).EnsureSuccessStatusCode();
        (await validator.PostActionAsync(id, "blocked", new VerdictRequest("win-validator", "No browser here. Tried opening the dashboard URL.", SubjectId: (await validator.GetTaskAsync(id)).Task.CurrentSubject!.Id))).EnsureSuccessStatusCode();

        return (owner, id);
    }

    /// <summary>A bounced-back task can resolve its stored branch after an absent or unhelpful hint.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("task/T-1-empty")]
    [InlineData("task/T-1-nonexistent")]
    public async Task A_task_that_knows_its_branch_resolves_it_when_the_hint_has_no_spec(string? branch)
    {
        var (owner, id) = await BlockedTaskWithBranchAsync("# T-1 — Build the feature\n" + TestRepo.Verification);
        _repo.Git("branch", "task/T-1-empty", "main");
        Assert.False(File.Exists(Path.Combine(_repo.Path, "specs/T-1-feature.md")));

        var task = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/T-1-feature.md", branch))).ReadTaskAsync();

        Assert.Equal("specs/T-1-feature.md", task.SpecPath);
    }

    /// <summary>
    /// The branch is a hint, never an assertion — which is what makes it safe for the CLI to fill in from
    /// wherever the caller happens to be standing. A branch that does not hold the file is passed over.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("task/T-1-elsewhere")]
    public async Task A_default_branch_spec_attaches_when_the_hint_has_no_spec(string? branch)
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Git("branch", "task/T-1-elsewhere", "main");   // branched before the spec existed
        _repo.Write("specs/T-1-feature.md", "# T-1 — Build the feature\n" + TestRepo.Verification);
        _repo.Commit("the spec, on main");

        var task = await (await owner.PostActionAsync(id, "spec",
            new SetSpecRequest("specs/T-1-feature.md", branch))).ReadTaskAsync();

        Assert.Equal("specs/T-1-feature.md", task.SpecPath);
    }

    /// <summary>The heading check is the point of this guard, so it must not become source-dependent.</summary>
    [Fact]
    public async Task A_spec_on_a_branch_is_still_refused_when_its_heading_names_another_task()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.BranchWithFile("task/T-9-gate", "specs/T-9-outbound-gate.md", "# T-9 — The outbound gate\n");

        var error = await RefusedAsync(owner, id, "specs/T-9-outbound-gate.md", "task/T-9-gate");

        Assert.Equal("spec_id_mismatch", error.Code);
        Assert.Contains("not T-1", error.Message);
    }

    [Fact]
    public async Task Invalid_supplied_branch_content_is_not_replaced_by_a_valid_working_tree_copy()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.BranchWithFile("task/T-1-feature", "specs/T-1-feature.md", "# T-9 — Another task\n");
        _repo.Write("specs/T-1-feature.md", "# T-1 — Build the feature\n");

        var error = await RefusedAsync(owner, id, "specs/T-1-feature.md", "task/T-1-feature");

        Assert.Equal("spec_id_mismatch", error.Code);
        Assert.Null((await owner.GetTaskAsync(id)).Task.SpecPath);
    }

    [Fact]
    public async Task Invalid_stored_branch_content_is_not_replaced_by_a_valid_working_tree_copy()
    {
        var (owner, id) = await BlockedTaskWithBranchAsync("# T-9 — Another task\n");
        _repo.Git("branch", "task/T-1-empty", "main");
        _repo.Write("specs/T-1-feature.md", "# T-1 — Build the feature\n");

        var error = await RefusedAsync(owner, id, "specs/T-1-feature.md", "task/T-1-empty");

        Assert.Equal("spec_id_mismatch", error.Code);
        Assert.Equal("specs/T-1-placeholder.md", (await owner.GetTaskAsync(id)).Task.SpecPath);
    }

    /// <summary>
    /// The message that cost the time: it told the caller to commit the spec on the task branch, which was
    /// exactly what they had just done. It now says where it looked instead.
    /// </summary>
    [Fact]
    public async Task A_spec_nowhere_to_be_found_says_where_it_looked()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Git("branch", "task/T-1-empty", "main");

        var error = await RefusedAsync(owner, id, "specs/typo.md", "task/T-1-empty");

        Assert.Equal("spec_missing", error.Code);
        Assert.Contains("branch 'task/T-1-empty'", error.Message);
        Assert.Contains("not in the working tree", error.Message);
        Assert.DoesNotContain("Commit the spec on the task branch first", error.Message);

        // And with no branch in play at all, it names the thing that would have helped.
        var bare = await RefusedAsync(owner, id, "specs/typo.md");
        Assert.Contains("pass --branch", bare.Message);
    }

    /// <summary>A branch read must not become a way to name something outside the repository.</summary>
    [Fact]
    public async Task A_path_that_escapes_the_repository_is_refused_even_with_a_branch()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Git("branch", "task/T-1-x", "main");

        var error = await RefusedAsync(owner, id, "../anywhere/T-1.md", "task/T-1-x");

        Assert.Equal("spec_outside_repository", error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("task/T-1-empty")]
    [InlineData("task/T-1-nonexistent")]
    public async Task An_uncommitted_working_tree_spec_attaches_when_the_hint_has_no_spec(string? branch)
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Git("branch", "task/T-1-empty", "main");
        _repo.Write("specs/T-1-feature.md", "# T-1 — Build the feature\n\nFrozen spec.\n" + TestRepo.Verification);

        var task = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/T-1-feature.md", branch))).ReadTaskAsync();

        Assert.Equal("specs/T-1-feature.md", task.SpecPath);
    }

    [Fact]
    public async Task A_spec_written_for_another_task_is_refused()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/T-9-outbound-gate.md", "# T-9 — The outbound gate\n\nFrozen spec.\n");

        var error = await RefusedAsync(owner, id, "specs/T-9-outbound-gate.md");

        Assert.Equal("spec_id_mismatch", error.Code);
        Assert.Contains("T-9", error.Message);
        Assert.Contains("not T-1", error.Message);
        Assert.Null((await owner.GetTaskAsync(id)).Task.SpecPath);
    }

    [Fact]
    public async Task A_path_to_no_file_is_refused()
    {
        var (owner, id) = await ClaimedTaskAsync();

        var error = await RefusedAsync(owner, id, "specs/typo.md");

        Assert.Equal("spec_missing", error.Code);
        Assert.Contains("specs/typo.md", error.Message);
    }

    [Fact]
    public async Task A_path_that_escapes_the_repository_is_refused()
    {
        var (owner, id) = await ClaimedTaskAsync();
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "T-1.md"), "# T-1 — somebody else's file\n");
        var sibling = _repo.Path + "-evil";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "T-1.md"), "# T-1 — a neighbour that merely shares a prefix\n");

        try
        {
            var up = await RefusedAsync(owner, id, $"../{Path.GetFileName(_outside)}/T-1.md");
            Assert.Equal("spec_outside_repository", up.Code);

            // The subtle one: '<repo>-evil' starts with '<repo>' but is not inside it.
            var neighbour = await RefusedAsync(owner, id, $"../{Path.GetFileName(sibling)}/T-1.md");
            Assert.Equal("spec_outside_repository", neighbour.Code);
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public async Task A_heading_that_names_no_task_is_accepted()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/outbound-gate.md", "# The outbound gate\n\nNot every project writes the id into the heading.\n" + TestRepo.Verification);

        var task = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/outbound-gate.md"))).ReadTaskAsync();

        Assert.Equal("specs/outbound-gate.md", task.SpecPath);
    }

    [Fact]
    public async Task Blank_lines_before_the_heading_do_not_hide_it()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/T-9-leading-space.md", "\n\n   \n# T-9 — The outbound gate\n");

        var error = await RefusedAsync(owner, id, "specs/T-9-leading-space.md");

        Assert.Equal("spec_id_mismatch", error.Code);
    }

    /// <summary>
    /// The check reads at most 8192 characters, so a heading pushed past that is never seen. Two of its
    /// requirements meet here — "the first non-blank line" and the cap — and the conservative resolution is to
    /// accept: refusing would mean claiming the file is another task's spec without having read a line of it.
    /// </summary>
    [Fact]
    public async Task A_heading_buried_past_the_read_cap_is_accepted_rather_than_blamed_on_another_task()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/T-9-buried-heading.md", new string(' ', 8193) + "\n# T-9 — The outbound gate\n" + TestRepo.Verification);

        var task = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/T-9-buried-heading.md"))).ReadTaskAsync();

        Assert.Equal("specs/T-9-buried-heading.md", task.SpecPath);
    }

    /// <summary>
    /// Founder request #15, option C: a spec says what it needs and the hub flags the task at freeze time,
    /// so the conductor never staffs a session that cannot run it. Eight validator sessions ended in
    /// 'blocked' for want of a browser before this existed, three of them on one task.
    /// </summary>
    [Theory]
    [InlineData("needs: browser")]
    [InlineData("  needs: browser")]
    [InlineData("- needs: browser")]
    [InlineData("> needs: browser")]
    [InlineData("**needs:** browser")]
    [InlineData("NEEDS: Browser")]
    public async Task A_spec_that_declares_what_it_needs_flags_the_task_when_it_is_frozen(string declaration)
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/T-1-eyes.md", $"# T-1 — Build it\n\n## Verification\n\n{declaration}\n\n```\necho verified\n```\n");

        var task = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/T-1-eyes.md"))).ReadTaskAsync();

        Assert.StartsWith("The spec declares 'needs: ", task.AttendedReason);
        Assert.Contains("waits for a human validator", task.AttendedReason);
        var recorded = Assert.Single((await owner.GetTaskAsync(id)).Events, e => e.Type == "task.attended");
        Assert.Equal("spec_needs", recorded.Payload.GetProperty("source").GetString());
    }

    [Theory]
    [InlineData("needs: headless-browser")]
    [InlineData("- **NEEDS:** `Headless-Browser`.")]
    [InlineData("> needs: HEADLESS-BROWSER")]
    public async Task Headless_alone_does_not_require_attendance(string declaration)
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/T-1-headless.md", $"# T-1 — Build it\n{declaration}\n" + TestRepo.Verification);

        var task = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/T-1-headless.md"))).ReadTaskAsync();

        Assert.Null(task.AttendedReason);
        Assert.DoesNotContain((await owner.GetTaskAsync(id)).Events, e => e.Type == "task.attended");
    }

    [Theory]
    [InlineData("needs: headless-browser\nneeds: browser", "browser")]
    [InlineData("needs: browser\nneeds: headless-browser", "browser")]
    [InlineData("needs: headless-browser\nneeds: device", "device")]
    [InlineData("needs: device\nneeds: headless-browser", "device")]
    [InlineData("needs: headless-browser-extra", "headless-browser-extra")]
    [InlineData("needs: headless-browser, browser", "headless-browser, browser")]
    public async Task Other_needs_still_require_attendance(string declarations, string need)
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/T-1-mixed.md", $"# T-1 — Build it\n{declarations}\n" + TestRepo.Verification);

        var task = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/T-1-mixed.md"))).ReadTaskAsync();

        Assert.Contains($"needs: {need}'", task.AttendedReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacing_a_spec_with_headless_preserves_existing_attendance(bool manual)
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/T-1-before.md", "# T-1 — Build it\nneeds: browser\n" + TestRepo.Verification);
        var before = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/T-1-before.md"))).ReadTaskAsync();
        var reason = before.AttendedReason;
        if (manual)
        {
            reason = "Needs human visual review";
            (await owner.PostActionAsync(id, "attended", new AttendedRequest(reason))).EnsureSuccessStatusCode();
        }
        _repo.Write("specs/T-1-after.md", "# T-1 — Build it\nneeds: headless-browser\n" + TestRepo.Verification);

        var after = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/T-1-after.md"))).ReadTaskAsync();

        Assert.Equal(reason, after.AttendedReason);
    }

    [Fact]
    public async Task A_spec_that_declares_nothing_leaves_the_task_alone()
    {
        var (owner, id) = await ClaimedTaskAsync();
        // Prose about browsers is not a declaration; only a line of its own is.
        _repo.Write("specs/T-1-quiet.md", "# T-1 — Build it\n\nNothing here needs a browser, and this sentence is not a declaration.\n" + TestRepo.Verification);

        var task = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/T-1-quiet.md"))).ReadTaskAsync();

        Assert.Null(task.AttendedReason);
        Assert.DoesNotContain((await owner.GetTaskAsync(id)).Events, e => e.Type == "task.attended");
    }

    /// <summary>
    /// A declaration is found wherever it is. The heading check reads only the first 8KB on purpose; a need
    /// the hub silently failed to see would be the worst of both worlds, so the read for it is not capped
    /// there.
    /// </summary>
    [Fact]
    public async Task A_declaration_past_the_heading_window_is_still_seen()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/T-1-late.md", "# T-1 — Build it\n" + new string('x', 20_000) + "\n\nneeds: browser\n" + TestRepo.Verification);

        var task = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/T-1-late.md"))).ReadTaskAsync();

        Assert.Contains("needs: browser", task.AttendedReason);
    }

    /// <summary>A human's own sentence is better than the generated one, so freezing a spec never overwrites it.</summary>
    [Fact]
    public async Task A_reason_somebody_wrote_survives_freezing_a_spec_that_declares_a_need()
    {
        var (owner, id) = await ClaimedTaskAsync();
        const string Written = "the collision pill renders inside Virtualize";
        (await owner.PostActionAsync(id, "attended", new AttendedRequest(Written))).EnsureSuccessStatusCode();
        _repo.Write("specs/T-1-eyes.md", "# T-1 — Build it\n\nneeds: browser\n" + TestRepo.Verification);

        var task = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/T-1-eyes.md"))).ReadTaskAsync();

        Assert.Equal(Written, task.AttendedReason);
    }

    /// <summary>
    /// The hole a validator found: a 1 MB cap does not remove the silent miss, it moves it to a bigger
    /// number. A spec with a megabyte of padding and then "needs: browser" was accepted with no flag, marked
    /// implemented, and actually staffed. A spec too big to read in full is now refused rather than read in
    /// part, so there is no size at which a declaration is quietly unseen.
    /// </summary>
    [Fact]
    public async Task A_spec_too_large_to_read_in_full_is_refused_rather_than_half_read()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/T-1-huge.md", "# T-1 - Build it" + new string('x', 1_050_000) + "needs: browser");

        var error = await RefusedAsync(owner, id, "specs/T-1-huge.md");

        Assert.Equal("spec_too_large", error.Code);
        Assert.Contains("larger than 1 MB", error.Message);
        Assert.Contains("silently missed", error.Message);
        Assert.Null((await owner.GetTaskAsync(id)).Task.SpecPath);   // nothing was attached
    }

    /// <summary>And the same spec on a branch, since that read has its own path to the cap.</summary>
    [Fact]
    public async Task A_spec_too_large_is_refused_when_it_comes_off_a_branch_too()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.BranchWithFile("task/T-1-huge", "specs/T-1-huge.md",
            "# T-1 - Build it" + new string('x', 1_050_000) + "needs: browser");

        var error = await RefusedAsync(owner, id, "specs/T-1-huge.md", "task/T-1-huge");

        Assert.Equal("spec_too_large", error.Code);
    }

    /// <summary>
    /// A spec that does not say how it is verified is a validator session spent working that out. It is refused
    /// once, when it is frozen, with the reason; specs attached before the rule are not re-read.
    /// </summary>
    [Fact]
    public async Task A_spec_with_no_verification_section_is_refused()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/T-1-silent.md", "# T-1 — Build it\n\n## Goal\n\nDo the thing.\n");

        var error = await RefusedAsync(owner, id, "specs/T-1-silent.md");

        Assert.Equal("spec_unverifiable", error.Code);
        Assert.Contains("## Verification", error.Message);
        Assert.Null((await owner.GetTaskAsync(id)).Task.SpecPath);
    }

    /// <summary>
    /// The historical "needs a live browser" spec: Verification prose that opens the dashboard and clicks, with no
    /// needs line. Eight validator sessions ended blocked on that shape before anything refused it.
    /// </summary>
    [Theory]
    [InlineData("Open the dashboard in a browser and click the board tab; the pill should render.")]
    [InlineData("Take a screenshot of the panel after the change.")]
    [InlineData("Drive it with Playwright and compare.")]
    public async Task Verification_prose_that_drives_a_browser_without_declaring_it_is_refused_with_the_line_quoted(string line)
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/T-1-live.md", $"# T-1 — Build it\n\n## Verification\n\n{line}\n\n```\ndotnet build\n```\n");

        var error = await RefusedAsync(owner, id, "specs/T-1-live.md");

        Assert.Equal("spec_unverifiable", error.Code);
        Assert.Contains($"\"{line}\"", error.Message);
        Assert.Contains("needs: headless-browser", error.Message);
    }

    [Theory]
    [InlineData("needs: headless-browser")]
    [InlineData("needs: browser")]
    public async Task Browser_prose_with_the_need_declared_is_accepted(string declaration)
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/T-1-declared.md", $"# T-1 — Build it\n\n{declaration}\n\n## Verification\n\nOpen the dashboard in a browser and click the board tab.\n");

        var task = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/T-1-declared.md"))).ReadTaskAsync();

        Assert.Equal("specs/T-1-declared.md", task.SpecPath);
    }

    [Fact]
    public async Task A_fenced_command_block_under_verification_passes_the_lint()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/T-1-checked.md", "# T-1 — Build it\n\n## Goal\n\nClick nothing; this is a service.\n\n## Verification\n\n```\ndotnet test\n```\n");

        var task = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/T-1-checked.md"))).ReadTaskAsync();

        Assert.Equal("specs/T-1-checked.md", task.SpecPath);
    }

    [Fact]
    public async Task An_unknown_validation_line_is_refused()
    {
        var (owner, id) = await ClaimedTaskAsync();
        _repo.Write("specs/T-1-vibes.md", "# T-1 — Build it\n\nvalidation: vibes\n" + TestRepo.Verification);

        var error = await RefusedAsync(owner, id, "specs/T-1-vibes.md");

        Assert.Equal("spec_validation_invalid", error.Code);
    }

    /// <summary>A spec right up against the cap is still ordinary work, and its declaration is still seen.</summary>
    [Fact]
    public async Task A_spec_just_inside_the_cap_is_read_in_full()
    {
        var (owner, id) = await ClaimedTaskAsync();
        var head = "# T-1 - Build it\n";
        var tail = "\nneeds: browser\n" + TestRepo.Verification;
        _repo.Write("specs/T-1-big.md", head + new string('x', 1_048_576 - head.Length - tail.Length) + tail);

        var task = await (await owner.PostActionAsync(id, "spec", new SetSpecRequest("specs/T-1-big.md"))).ReadTaskAsync();

        Assert.Contains("needs: browser", task.AttendedReason);
    }
}
