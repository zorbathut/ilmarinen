using Ilmarinen.Protocol;
using Ilmarinen.Server.Hubs;
using NUnit.Framework;
using System.Linq;
using System.Reflection;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>The worker protocol's shape is taken from IWorkerHub, so a method workers can call has to be declared there: one only on the hub would be callable, but outside the shape, and change without a version bump.</summary>
[TestFixture]
public class WorkerHubContractTests
{
    [Test]
    public void EveryMethodWorkersCanCall_IsDeclaredInIWorkerHub()
    {
        // SignalR exposes a hub's public instance methods; the Hub base class's lifecycle overrides aren't callable.
        var callable = typeof(WorkerHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetBaseDefinition().DeclaringType == typeof(WorkerHub))
            .Select(m => m.Name)
            .Order()
            .ToList();
        var declared = typeof(IWorkerHub).GetMethods().Select(m => m.Name).Order().ToList();

        Assert.That(callable, Is.EqualTo(declared));
    }
}
