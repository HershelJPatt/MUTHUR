using Muthur.Launch;

namespace Muthur.Launch.Tests;

public sealed class VerificationEnvironmentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "muthur-env-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => VerificationFiles.DeleteOwned(root, Path.GetDirectoryName(root)!);

    [Fact]
    public async Task Owned_git_policy_blocks_parent_discovery_and_supports_long_repositories()
    {
        var scratch = Path.Combine(root, "scratch");
        var env = VerificationEnvironment.Create(scratch, "http://127.0.0.1:17420");
        var runner = new ProcessRunner();
        async Task<ProcessResult> Git(string directory, params string[] args) =>
            await runner.RunAsync("git", args, directory, scrubEnvironment: VerificationEnvironment.Scrub(), environment: env);
        Assert.True((await Git(root, "init", "--quiet")).Ok);
        Assert.False((await Git(env["TEMP"], "rev-parse", "--show-toplevel")).Ok);
        Assert.Equal(VerificationEnvironment.GitConfiguration, File.ReadAllText(Path.Combine(env["HOME"], ".gitconfig")));
        Assert.DoesNotContain("GIT_CONFIG_COUNT", env.Keys);
        var repo = Path.Combine(scratch, new string('a', 60), new string('b', Math.Max(20, 225 - scratch.Length - 62)));
        Directory.CreateDirectory(repo);
        var init = await Git(repo, "init", "--quiet");
        Assert.True(init.Ok, init.Message);
        Assert.True((await Git(repo, "rev-parse", "--show-toplevel")).Ok);
        Assert.True((await Git(repo, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--allow-empty", "-m", "fixture")).Ok);
        var checkout = Path.Combine(scratch, "checkout");
        Assert.True((await Git(repo, "worktree", "add", "--detach", checkout, "HEAD")).Ok);
        Assert.True((await Git(checkout, "rev-parse", "--show-toplevel")).Ok);
    }

    [Fact]
    public void Cache_identity_canonicalizes_owned_roots_but_tracks_policy_bytes()
    {
        var first = VerificationEnvironment.Create(Path.Combine(root, "a"), "http://127.0.0.1:17420");
        var second = VerificationEnvironment.Create(Path.Combine(root, "b"), "http://127.0.0.1:17421");
        Assert.Equal(VerificationEnvironment.Identity(first), VerificationEnvironment.Identity(second));
        File.AppendAllText(Path.Combine(second["HOME"], ".gitconfig"), "# policy changed\n");
        Assert.NotEqual(VerificationEnvironment.Identity(first)["environment/git-config"], VerificationEnvironment.Identity(second)["environment/git-config"]);
    }
}
