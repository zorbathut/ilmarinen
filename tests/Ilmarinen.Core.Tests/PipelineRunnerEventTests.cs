using Docker.DotNet;
using Ilmarinen.Docker;
using Ilmarinen.Scripting;
using NUnit.Framework;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Core.Tests;

/// <summary>What a run tells an observer such as the resource sampler: each step's start and end in order, its container while it still exists, and which steps run an image an earlier step produced.</summary>
[TestFixture]
[Parallelizable(ParallelScope.Children)]
public class PipelineRunnerEventTests
{
    [Test]
    public async Task RunAsync_RaisesEachStepsEvents_InOrder_WithItsContainerStillThereAtTheEnd()
    {
        var (events, containerExistedAtEnd, progressDuringStep) = await RunAsync("""
            var first = Step<ImageRef>("produce")
                .Image("alpine:latest")
                .Run(async ctx => {
                    await ctx.Shell("true");
                    return ImageRef.From("alpine:latest");
                });

            Step("consume")
                .Image(() => first.Output!)
                .Run(async ctx => {
                    await ctx.Shell("exit 3");
                });
            """);

        var steps = events.Where(e => e.Kind is PipelineEventKind.StepStarted or PipelineEventKind.StepContainerEnding or PipelineEventKind.StepEnded).ToList();
        Assert.That(steps.Select(e => (e.Kind, e.StepIndex)), Is.EqualTo(new (PipelineEventKind, int?)[]
        {
            (PipelineEventKind.StepStarted, 0),
            (PipelineEventKind.StepContainerEnding, 0),
            (PipelineEventKind.StepEnded, 0),
            (PipelineEventKind.StepStarted, 1),
            (PipelineEventKind.StepContainerEnding, 1),
            (PipelineEventKind.StepEnded, 1),
        }));
        Assert.That(steps.Where(e => e.Kind == PipelineEventKind.StepEnded).Select(e => e.Status), Is.EqualTo(new[] { "Success", "Failed" }));
        Assert.That(steps.Where(e => e.Kind == PipelineEventKind.StepStarted).Select(e => e.ImageFromEarlierStep), Is.EqualTo(new bool?[] { false, true }), "the second step runs the image the first returned");
        Assert.That(containerExistedAtEnd, Is.EqualTo(new[] { true, true }), "the end of a step's container is announced before it is removed");
        Assert.That(progressDuringStep.All(p => p is { StepContainerId: not null, NetworkName: not null }), Is.True, "while a step runs, progress names its container and network");
    }

    private static async Task<(List<PipelineEvent> Events, List<bool> ContainerExistedAtEnd, List<PipelineProgress?> ProgressDuringStep)> RunAsync(string script)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"ilmarinen-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var scriptPath = Path.Combine(workDir, "events.csx");
            await File.WriteAllTextAsync(scriptPath, script);
            var loaded = await PipelineScript.LoadAsync(scriptPath);

            using var docker = DockerClientFactory.Create();
            var events = new List<PipelineEvent>();
            var existed = new List<bool>();
            var progress = new List<PipelineProgress?>();
            PipelineRunner? runner = null;
            runner = new PipelineRunner(workDir: workDir, onEvent: async e =>
            {
                events.Add(e);
                if (e.Kind == PipelineEventKind.StepContainerEnding)
                {
                    progress.Add(runner!.Progress);
                    try
                    {
                        await docker.Containers.InspectContainerAsync(e.ContainerId!);
                        existed.Add(true);
                    }
                    catch (DockerContainerNotFoundException)
                    {
                        existed.Add(false);
                    }
                }
            });
            using (runner)
            {
                await runner.RunAsync(loaded.Steps);
            }
            return (events, existed, progress);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
