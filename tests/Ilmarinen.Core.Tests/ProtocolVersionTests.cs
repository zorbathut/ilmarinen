using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Linq;
using System;

namespace Ilmarinen.Core.Tests;

[TestFixture]
public class ProtocolVersionTests
{
    // The hash is a wire contract: a server whose hash differs from a worker's rejects it outright, and a launcher-run
    // worker can't learn the new bundle across a protocol break. Changing it is a deliberate act — update this constant
    // in the same commit that changes the protocol, so the break shows up in review rather than in the fleet.
    private const string CurrentHash = "5C19A20B7C79";

    [Test]
    public void Hash_MatchesTheRecordedContract()
    {
        Assert.That(ProtocolVersion.Hash, Is.EqualTo(CurrentHash), $"protocol shape changed:\n{ProtocolVersion.HashInput}");
    }

    // A type that carries nothing is not a wire type — it is a helper that reached the hash by sitting in a seeded
    // namespace, which silently locks out every deployed worker for a change that cannot affect them. Checked
    // separately from the hash itself because the two failures want opposite responses: a deliberate protocol change
    // updates the constant above, this one means the seeding is wrong and the constant must not be touched.
    [Test]
    public void HashInput_CoversOnlyTypesThatCarrySomething()
    {
        var empty = ProtocolVersion.HashInput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.Contains(' '))
            .ToList();

        Assert.That(empty, Is.Empty, "these hashed types have no members, so they are not on the wire");
    }
}
