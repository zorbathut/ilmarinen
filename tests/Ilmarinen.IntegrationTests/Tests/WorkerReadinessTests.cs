using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using NUnit.Framework;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>The hold the server goes by: the worker's own, except someone using its machine when its operator has said to take jobs anyway and it speaks a protocol that lets it.</summary>
[TestFixture]
public class WorkerReadinessTests
{
    private const int Older = 1;

    [Test]
    public void EffectiveHold_WaivesOnlyInUse_OnlyEvenWhileInUse_AndOnlyForAWorkerThatCanTakeTheJob()
    {
        var current = WorkerProtocol.Version;
        Assert.Multiple(() =>
        {
            Assert.That(WorkerReadiness.EffectiveHold(WorkerHold.InUse, WorkerMode.EvenWhileInUse, current), Is.Null);
            Assert.That(WorkerReadiness.EffectiveHold(WorkerHold.InUse, WorkerMode.EvenWhileInUse, Older), Is.EqualTo(WorkerHold.InUse), "an older worker would hand the job back");
            Assert.That(WorkerReadiness.EffectiveHold(WorkerHold.InUse, WorkerMode.Normal, current), Is.EqualTo(WorkerHold.InUse));
            Assert.That(WorkerReadiness.EffectiveHold(WorkerHold.InUse, WorkerMode.Paused, current), Is.EqualTo(WorkerHold.InUse));
            foreach (var mode in new[] { WorkerMode.Normal, WorkerMode.Paused, WorkerMode.EvenWhileInUse })
            {
                foreach (var version in new[] { Older, current })
                {
                    Assert.That(WorkerReadiness.EffectiveHold(null, mode, version), Is.Null, $"{mode} v{version}: no hold");
                    Assert.That(WorkerReadiness.EffectiveHold(WorkerHold.Sleeping, mode, version), Is.EqualTo(WorkerHold.Sleeping), $"{mode} v{version}");
                    Assert.That(WorkerReadiness.EffectiveHold(WorkerHold.ShuttingDown, mode, version), Is.EqualTo(WorkerHold.ShuttingDown), $"{mode} v{version}");
                }
            }
        });
    }
}
