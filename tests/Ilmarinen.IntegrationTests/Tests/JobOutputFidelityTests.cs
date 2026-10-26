using Ilmarinen.Database;
using Ilmarinen.IntegrationTests.Fixtures;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// What a step's commands write is what the job log stores: every byte, once, on the stream it was written to.
/// </summary>
[TestFixture]
[Category("Integration")]
public class JobOutputFidelityTests
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

    /// <summary>Output reaches the worker in fixed-size reads, and a character whose bytes straddle two of them must survive the boundary.</summary>
    [Test]
    public async Task Exec_MultiByteCharacterAcrossAReadBoundary_IsLoggedIntact()
    {
        // 4095 ASCII bytes and then a two-byte character, so the character's bytes sit either side of byte 4096 — and a read never returns more than 4096.
        var (stdout, _) = await RunStepAsync("alpine:latest", @"head -c 4095 /dev/zero | tr '\0' a > /tmp/out && printf '\303\251\n' >> /tmp/out && cat /tmp/out");

        Assert.That(stdout, Does.Contain(new string('a', 4095) + "é\n"));
        Assert.That(stdout, Does.Not.Contain("�"));
    }

    /// <summary>A nested run's output is the step's own output, so it should read exactly as the nested command wrote it and appear once.</summary>
    [Test]
    public async Task AgentRun_NestedOutput_IsLoggedOnceAndExactly()
    {
        var (stdout, stderr) = await RunStepAsync("docker:cli", @"ilmarinen-agent run alpine:latest -- sh -c 'printf ""a>b\n\n\033[31mred+\303\251\n""; printf ""nested-err\n"" >&2; exit 3'; echo rc=$?");

        // Counted on bare markers as well as the exact text, since a second, mangled copy wouldn't match the exact text.
        Assert.That(Occurrences(stdout + stderr, "red"), Is.EqualTo(1), $"stdout was: {stdout}");
        Assert.That(Occurrences(stdout + stderr, "nested-err"), Is.EqualTo(1), $"stderr was: {stderr}");
        Assert.That(stdout, Does.Contain("a>b\n\n\u001b[31mred+é\n"));
        Assert.That(stderr, Does.Contain("nested-err\n"));
        Assert.That(stdout, Does.Contain("rc=3\n"), "the nested exit code should reach the step");
    }

    /// <summary>Callers capture a build's stdout as the image reference, so the build's own output has to go elsewhere.</summary>
    [Test]
    public async Task AgentBuild_StdoutIsOnlyTheImageReference()
    {
        var tag = $"ilmarinen-test-build:{Guid.NewGuid():N}";
        var (stdout, stderr) = await RunStepAsync("docker:cli", $"mkdir -p /tmp/ctx && echo 'FROM alpine:latest' > /tmp/ctx/Dockerfile && rc=0 && ref=$(ilmarinen-agent build -t {tag} /tmp/ctx) || rc=$?; docker rmi {tag} >/dev/null 2>&1; echo \"ref=[$ref]\"; exit $rc");

        Assert.That(stdout, Does.Contain($"ref=[{tag}]\n"));
        Assert.That(stderr, Does.Contain("alpine"), "the build's progress is reported, on stderr");
    }

    /// <summary>Runs a one-step pipeline whose step runs <paramref name="script"/> in <paramref name="image"/>, and returns the job's logged stdout and stderr.</summary>
    private async Task<(string Stdout, string Stderr)> RunStepAsync(string image, string script)
    {
        _repo.AddFile("pipeline.csx", $$""""
            Step("output")
                .Image("{{image}}")
                .Run(async ctx => {
                    await ctx.Shell("""{{script}}""");
                });
            """");
        _repo.Commit("Add pipeline");

        var jobId = await _fixture.SubmitJobAsync(new JobSubmission
        {
            RepoUrl = _repo.Url,
            Ref = "master",
            ScriptPath = "pipeline.csx"
        });

        var job = await _fixture.WaitForJobCompletionAsync(jobId);
        Assert.That(job.Status, Is.EqualTo(JobStatus.Success), await _fixture.GetJobLogsAsync(jobId));

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        var chunks = await db.JobLogChunks
            .Where(c => c.JobId == jobId)
            .OrderBy(c => c.SequenceNumber)
            .Select(c => c.Content)
            .ToListAsync();
        var entries = chunks.SelectMany(LogChunkParser.ParseEntries).ToList();

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        foreach (var entry in entries)
        {
            if (entry.Type == "o")
            {
                stdout.Append(entry.Data);
            }
            else if (entry.Type == "e")
            {
                stderr.Append(entry.Data);
            }
        }

        return (stdout.ToString(), stderr.ToString());
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
