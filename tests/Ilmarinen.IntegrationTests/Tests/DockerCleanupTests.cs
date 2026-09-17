using Ilmarinen.Docker;
using NUnit.Framework;
using System.Collections.Generic;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
public class DockerCleanupTests
{
    private const string OwnNamespace = "boot/pid:[4026531836]";

    private static readonly Func<int, bool> Dead = _ => false;
    private static readonly Func<int, bool> Alive = _ => true;

    // The diagnostic's scratch networks are labelled under their own prefix and leak the same way a pipeline network does.
    [TestCase("ilmarinen.test")]
    [TestCase("ilmarinen.diagnostic")]
    public void IsStaleNetwork_DeadOwnerInOurNamespace_IsStale(string prefix)
    {
        var labels = Labels(pid: "4242", pidNamespace: OwnNamespace, prefix: prefix);

        Assert.That(DockerCleanup.IsStaleNetwork(labels, OwnNamespace, Dead), Is.True);
    }

    [Test]
    public void IsStaleNetwork_LiveOwnerInOurNamespace_IsNotStale()
    {
        var labels = Labels(pid: "4242", pidNamespace: OwnNamespace);

        Assert.That(DockerCleanup.IsStaleNetwork(labels, OwnNamespace, Alive), Is.False);
    }

    // A worker running in a container labels its networks with an in-container PID, which reads as dead from outside while its job is live.
    [TestCase("boot/pid:[4026532850]")]
    [TestCase("other-boot/pid:[4026531836]")]
    [TestCase(null)]
    public void IsStaleNetwork_OwnerOutsideOurNamespace_IsNotStale(string? pidNamespace)
    {
        var labels = Labels(pid: "193", pidNamespace: pidNamespace);

        Assert.That(DockerCleanup.IsStaleNetwork(labels, OwnNamespace, Dead), Is.False, "a PID from another namespace says nothing about whether its run is still going");
    }

    [TestCase("not-a-pid")]
    [TestCase(null)]
    public void IsStaleNetwork_NoUsablePid_IsNotStale(string? pid)
    {
        var labels = Labels(pid: pid, pidNamespace: OwnNamespace);

        Assert.That(DockerCleanup.IsStaleNetwork(labels, OwnNamespace, Dead), Is.False);
    }

    [Test]
    public void GetPidNamespaceId_OnLinux_CombinesBootIdAndNamespace()
    {
        Assume.That(OperatingSystem.IsLinux());

        Assert.That(LinuxInterop.GetPidNamespaceId(), Does.Match(@"^[0-9a-f-]{36}/pid:\[\d+\]$"));
    }

    private static Dictionary<string, string> Labels(string? pid, string? pidNamespace, string prefix = "ilmarinen.test")
    {
        var labels = new Dictionary<string, string>();
        if (pid != null)
        {
            labels[$"{prefix}.pid"] = pid;
        }
        if (pidNamespace != null)
        {
            labels[$"{prefix}.pidns"] = pidNamespace;
        }
        return labels;
    }
}
