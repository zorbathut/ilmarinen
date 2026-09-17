using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Linq;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>
/// Skips a test that runs the real diagnostic when the diagnostic failed on this host's network rather than on anything the test is about: a resolver that stopped answering or a registry that timed out says nothing about the code under test.
/// </summary>
public static class DiagnosticPrecondition
{
    public static void IgnoreIfHostNetworkFailed(DiagnosticReport report)
    {
        var failure = report.Steps.FirstOrDefault(s => !s.Success
            && (s.Failure == FailureKind.WorkerDnsFailure || s.Failure == FailureKind.RegistryUnreachable || s.Failure == FailureKind.Timeout));

        if (failure != null)
        {
            Assert.Ignore($"this host's network failed the diagnostic's {failure.Name} step: {failure.Message}");
        }
    }
}
