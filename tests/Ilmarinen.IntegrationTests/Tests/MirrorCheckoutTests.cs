using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using NUnit.Framework;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Jobs are checked out from a per-repository mirror on the worker, with the pipeline script compiled straight from the pinned commit.
/// </summary>
[TestFixture]
[Category("Integration")]
public class MirrorCheckoutTests
{
    private IntegrationTestFixture _fixture = null!;
    private TestGitRepository _repo = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new IntegrationTestFixture();
        await _fixture.SetupAsync();
        await _fixture.StartWorkerAsync();

        _repo = new TestGitRepository();
    }

    [TearDown]
    public async Task TearDown()
    {
        _repo.Dispose();
        await _fixture.DisposeAsync();
    }

    [Test]
    public async Task TwoJobs_ShareOneMirrorAndLeaveNoBuildDirectory()
    {
        _repo.AddFile("pipeline.csx", """
            Step("check")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("echo", "hi"));
            """);
        _repo.Commit("Add pipeline");

        await RunAsync("pipeline.csx");
        await RunAsync("pipeline.csx");

        Assert.That(Directory.GetDirectories(MirrorsPath()), Has.Length.EqualTo(1));
        Assert.That(Directory.GetDirectories(_fixture.WorkerWorkspacePath!).Select(Path.GetFileName).Where(n => Guid.TryParse(n, out _)), Is.Empty);
    }

    [Test]
    public async Task Script_LoadsFilesBesideIt()
    {
        _repo.AddFile("build/common.csx", """
            void AddCheck(string message)
            {
                Step("check")
                    .Image("alpine:latest")
                    .Run(async ctx => await ctx.Exec("echo", message));
            }
            """);
        _repo.AddFile("build/pipeline.csx", """
            #load "common.csx"
            AddCheck("loaded");
            """);
        _repo.Commit("Add pipeline");

        var jobId = await RunAsync("build/pipeline.csx");

        Assert.That(await _fixture.GetJobLogsAsync(jobId), Does.Contain("loaded"));
    }

    [Test]
    public async Task MissingScript_FailsTheJob()
    {
        _repo.AddFile("pipeline.csx", "");
        _repo.Commit("Add pipeline");

        var jobId = await _fixture.SubmitJobAsync(Submission("nope.csx"));
        var job = await _fixture.WaitForJobCompletionAsync(jobId);

        Assert.That(job.Status, Is.EqualTo(JobStatus.Failed));
    }

    /// <summary>A step's own git sees the checkout as an ordinary clone of the job's repository, the second run of a persistent workspace included.</summary>
    [Test]
    public async Task StepGit_SeesAnOrdinaryCloneOfTheRepository([Values] bool persistent)
    {
        var workspace = persistent ? "Workspace(\"git-check\");\n" : "";
        _repo.AddFile("pipeline.csx", workspace + $$""""
            Step("git")
                .Image("buildpack-deps:bookworm-scm")
                .Run(async ctx => await ctx.Shell("""
                    set -e
                    git config --global --add safe.directory '*'
                    git remote get-url origin
                    test "$(git remote get-url origin)" = '{{_repo.Url}}'
                    git describe --tags --always
                    test "$(git rev-parse --abbrev-ref origin/HEAD)" = origin/master
                    git status --porcelain
                    test -z "$(git status --porcelain)"
                    """));
            """");
        _repo.Commit("Add pipeline");

        await RunAsync("pipeline.csx");
        _repo.AddFile("other.txt", "x");
        _repo.Commit("Second commit");
        await RunAsync("pipeline.csx");
    }

    /// <summary>Damage to the mirror surfaces wherever its objects are first read; the job gets a fresh mirror and runs.</summary>
    [Test]
    public async Task DamagedMirror_IsRebuiltAndTheJobRuns()
    {
        _repo.AddFile("pipeline.csx", """
            Step("check")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("echo", "hi"));
            """);
        _repo.Commit("Add pipeline");
        await RunAsync("pipeline.csx");

        var mirror = Directory.GetDirectories(MirrorsPath()).Single();
        foreach (var file in Directory.GetFiles(Path.Combine(mirror, "objects"), "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.WriteAllText(file, "garbage");
        }

        await RunAsync("pipeline.csx");

        Assert.That(Directory.GetDirectories(MirrorsPath()), Is.EqualTo(new[] { mirror }));
    }

    /// <summary>Losing an older pack leaves the refs readable; the damage surfaces only when the script's blob is read, and still gets the mirror rebuilt.</summary>
    [Test]
    public async Task MirrorMissingAnOlderPack_IsRebuiltAndTheJobRuns()
    {
        _repo.AddFile("pipeline.csx", """
            Step("check")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("echo", "hi"));
            """);
        _repo.Commit("Add pipeline");
        await RunAsync("pipeline.csx");
        var packDir = Path.Combine(Directory.GetDirectories(MirrorsPath()).Single(), "objects", "pack");
        var firstPack = Directory.GetFiles(packDir, "*.pack").Single();

        _repo.AddFile("other.txt", "x");
        _repo.Commit("Unrelated change");
        await RunAsync("pipeline.csx");
        File.Delete(firstPack);
        File.Delete(Path.ChangeExtension(firstPack, ".idx"));

        await RunAsync("pipeline.csx");
    }

    /// <summary>A persistent workspace a step left broken fails its job without costing the mirror a rebuild, which couldn't help.</summary>
    [Test]
    public async Task BrokenPersistentWorkspace_FailsWithoutRebuildingTheMirror()
    {
        _repo.AddFile("pipeline.csx", """
            Workspace("broken");
            Step("check")
                .Image("alpine:latest")
                .Run(async ctx => await ctx.Exec("echo", "hi"));
            """);
        _repo.Commit("Add pipeline");
        await RunAsync("pipeline.csx");
        var marker = Path.Combine(Directory.GetDirectories(MirrorsPath()).Single(), "marker");
        File.WriteAllText(marker, "");
        File.WriteAllText(Path.Combine(_fixture.WorkerWorkspacePath!, "broken", ".git", "index.lock"), "");

        var jobId = await _fixture.SubmitJobAsync(Submission("pipeline.csx"));
        var job = await _fixture.WaitForJobCompletionAsync(jobId);

        Assert.That(job.Status, Is.EqualTo(JobStatus.Failed));
        Assert.That(File.Exists(marker), Is.True, "the mirror was rebuilt");
    }

    private async Task<Guid> RunAsync(string scriptPath)
    {
        var jobId = await _fixture.SubmitJobAsync(Submission(scriptPath));
        var job = await _fixture.WaitForJobCompletionAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Success), await _fixture.GetJobLogsAsync(jobId));
        return jobId;
    }

    private JobSubmission Submission(string scriptPath)
    {
        return new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = scriptPath
        };
    }

    private string MirrorsPath()
    {
        return Path.Combine(_fixture.WorkerWorkspacePath!, ".mirrors");
    }
}
