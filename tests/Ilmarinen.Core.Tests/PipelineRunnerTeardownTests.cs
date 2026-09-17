using Docker.DotNet.Models;
using Ilmarinen.Docker;
using Ilmarinen.Scripting;
using NUnit.Framework;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Core.Tests;

/// <summary>
/// A graceful stop sends SIGTERM that no step process ever receives and then sits out the stop timeout, so step containers must go straight to SIGKILL. Checked through the daemon's events rather than wall-clock time, which a loaded Docker daemon stretches unpredictably.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.Children)]
public class PipelineRunnerTeardownTests
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(90);

    [Test]
    public async Task RunAsync_StepFinishes_KillsContainerWithoutStoppingIt()
    {
        var events = await RunWatchingStepContainerAsync("hostname > /workspace/cid", cancelOnceStarted: false);

        AssertKilledWithoutStop(events);
    }

    [Test]
    public async Task RunAsync_Cancelled_KillsContainerWithoutStoppingIt()
    {
        var events = await RunWatchingStepContainerAsync("hostname > /workspace/cid && sleep 300", cancelOnceStarted: true);

        AssertKilledWithoutStop(events);
    }

    private static void AssertKilledWithoutStop(IReadOnlyList<Message> events)
    {
        Assert.That(events.Select(e => e.Action), Does.Not.Contain("stop"), "a graceful stop only waits out its timeout");
        Assert.That(events.Where(e => e.Action == "kill").Select(e => e.Actor.Attributes["signal"]), Is.Not.Empty.And.All.EqualTo("9"), "the container should be killed with SIGKILL and nothing else");
    }

    /// <summary>Runs a one-step pipeline whose shell writes the container's ID (its hostname) to the workspace, and returns the daemon's kill, stop and destroy events for that container once it has been destroyed.</summary>
    private static async Task<IReadOnlyList<Message>> RunWatchingStepContainerAsync(string shell, bool cancelOnceStarted)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"ilmarinen-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            var scriptPath = Path.Combine(workDir, "teardown.csx");
            await File.WriteAllTextAsync(scriptPath, $$"""
                Step("probe")
                    .Image("alpine:latest")
                    .Run(async ctx => {
                        await ctx.Shell("{{shell}}");
                    });
                """);
            var cidPath = Path.Combine(workDir, "cid");

            // Watch from a second before the run, so events the daemon emits before the stream is established are replayed rather than missed.
            using var client = DockerClientFactory.Create();
            using var watchCts = new CancellationTokenSource(Cap);
            var events = new List<Message>();
            var watch = client.System.MonitorEventsAsync(
                new ContainerEventsParameters
                {
                    Since = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1).ToString(),
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["type"] = new Dictionary<string, bool> { ["container"] = true },
                        ["event"] = new Dictionary<string, bool> { ["kill"] = true, ["stop"] = true, ["destroy"] = true }
                    }
                },
                new SyncProgress<Message>(m =>
                {
                    lock (events)
                    {
                        events.Add(m);
                    }
                }),
                watchCts.Token);

            var scriptResult = await PipelineScript.LoadAsync(scriptPath);
            using var runner = new PipelineRunner(workDir: workDir);
            using var runCts = new CancellationTokenSource();
            var run = runner.RunAsync(scriptResult.Steps, runCts.Token);

            if (cancelOnceStarted)
            {
                while (!File.Exists(cidPath) || new FileInfo(cidPath).Length == 0)
                {
                    Assert.That(run.IsCompleted, Is.False, "the step should still be sleeping when it gets cancelled");
                    await Task.Delay(100);
                }
                await runCts.CancelAsync();
            }

            await run.WaitAsync(Cap);
            var cid = (await File.ReadAllTextAsync(cidPath)).Trim();
            Assert.That(cid, Has.Length.EqualTo(12), "the step container's hostname should be its short ID");

            // Waiting for destroy means a missed or broken subscription fails by timing out, never by passing with no events.
            var deadline = DateTime.UtcNow + Cap;
            List<Message> mine;
            while (true)
            {
                lock (events)
                {
                    mine = events.Where(e => e.Actor.ID.StartsWith(cid, StringComparison.Ordinal)).ToList();
                }
                if (mine.Any(e => e.Action == "destroy"))
                {
                    break;
                }
                Assert.That(watch.IsFaulted, Is.False, () => $"the event watch failed: {watch.Exception}");
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the step container was never destroyed");
                await Task.Delay(100);
            }

            await watchCts.CancelAsync();
            try
            {
                await watch;
            }
            catch (OperationCanceledException) when (watchCts.IsCancellationRequested)
            {
                // The event stream only ever ends by being cancelled.
            }
            return mine;
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>Reports on the caller's thread; Progress&lt;T&gt; would post to the thread pool, so an event could arrive after the check that looks for it.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public SyncProgress(Action<T> report)
        {
            _report = report;
        }

        public void Report(T value)
        {
            _report(value);
        }
    }
}
