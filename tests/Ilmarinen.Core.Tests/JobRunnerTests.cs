using Ilmarinen.Protocol.Responses;
using Ilmarinen.Worker.Services;
using Ilmarinen.Worker;
using LibGit2Sharp;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using System.IO;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Core.Tests;

[TestFixture]
public class JobRunnerTests
{
    private string _root = null!;
    private string _repoDir = null!;
    private HubConnection _connection = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"jobrunner-test-{Guid.NewGuid():N}");
        _repoDir = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repoDir);

        // Never started, so the connection is down the whole time — the state a worker is in while the server restarts under it.
        _connection = new HubConnectionBuilder().WithUrl("http://localhost:1").Build();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _connection.DisposeAsync();

        if (Directory.Exists(_root))
        {
            ForceDeleteDirectory(_root);
        }
    }

    [Test]
    public async Task ExecuteAsync_ServerUnreachableWhenCommitIsReported_BuffersTheReportAndCarriesOn()
    {
        var headSha = InitRepoWithOneCommit();

        var config = new WorkerConfig
        {
            ServerUrl = "http://localhost:1",
            WorkerKey = "unused",
            WorkspacePath = Path.Combine(_root, "workspaces")
        };
        var job = new JobAssignment
        {
            Id = Guid.NewGuid(),
            RepoUrl = _repoDir,
            Ref = "master",
            // Missing on purpose: the runner stops right after reporting the commit, before anything would need Docker.
            ScriptPath = "missing.csx"
        };

        var buffer = new MessageBuffer();
        var logger = new LoggerCapture();
        var sender = new BufferedHubSender(_connection, buffer, logger);
        var runner = new JobRunner(
            config,
            new WorkspaceManager(config, NullLogger<WorkspaceManager>.Instance),
            job,
            sender,
            logger,
            new LogCollector(job.Id, sender, logger));

        await runner.ExecuteAsync();

        // Buffering alone isn't the fix: the runner has to carry on past the report, as far as discovering the script is missing.
        Assert.That(logger.Entries, Has.Some.Matches<LoggerCapture.Entry>(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error && e.Message.Contains(job.ScriptPath)),
            "the job must go on after a commit report the server couldn't take");

        var buffered = buffer.TryDequeue(out var message) ? message : null;
        Assert.That(buffered?.Method, Is.EqualTo("ReportCommit"),
            "a commit report the server can't take right now must wait for the next reconnect, not fail the job");
        Assert.That(buffered!.Args, Is.EqualTo(new object[] { job.Id, headSha }));
        Assert.That(buffer.IsEmpty, Is.True);
    }

    private string InitRepoWithOneCommit()
    {
        Repository.Init(_repoDir);
        using var repo = new Repository(_repoDir);
        repo.Refs.UpdateTarget("HEAD", "refs/heads/master");

        File.WriteAllText(Path.Combine(_repoDir, "README"), "test");
        Commands.Stage(repo, "README");
        var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
        return repo.Commit("initial", signature, signature).Sha;
    }

    private static void ForceDeleteDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
