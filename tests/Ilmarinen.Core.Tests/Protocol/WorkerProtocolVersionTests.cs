using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System;

namespace Ilmarinen.Core.Tests.Protocol;

/// <summary>
/// The worker protocol's recorded shapes (src/Ilmarinen.Protocol/WorkerShapes/v{N}.txt) against the code: a change to the shape has to be recorded under a version, and a version the server still supports has to stay speakable. CLAUDE.md ("Protocol compatibility") says when to bump.
/// </summary>
[TestFixture]
public class WorkerProtocolVersionTests
{
    private const string WriteVariable = "ILMARINEN_WRITE_SHAPE";

    [Test]
    public void TheCurrentShape_IsRecordedUnderTheCurrentVersion()
    {
        var path = ShapePath(WorkerProtocol.Version);
        if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
        {
            File.WriteAllText(path, WorkerProtocol.ShapeText);
        }

        var recorded = File.Exists(path) ? File.ReadAllText(path) : null;
        Assert.That(recorded, Is.EqualTo(WorkerProtocol.ShapeText),
            $"The worker protocol's shape differs from {path}. If v{WorkerProtocol.Version} has shipped (the server's GET /api/version reports it; ask if you can't tell), bump WorkerProtocol.Version and seal v{WorkerProtocol.Version}'s hash in WorkerProtocol.Sealed; only if it hasn't may this version's record be amended. Either way, rerun with {WriteVariable}=1 to write the file.\n\nThe current shape:\n{WorkerProtocol.ShapeText}");
    }

    [Test]
    public void EverySupportedVersion_CanStillTalkToTheCurrentOne()
    {
        var breaks = Enumerable.Range(WorkerProtocol.MinSupportedVersion, WorkerProtocol.Version - WorkerProtocol.MinSupportedVersion)
            .SelectMany(v => WorkerShapeDiff.Breaks(File.ReadAllText(ShapePath(v)), WorkerProtocol.ShapeText).Select(b => $"v{v}: {b}"))
            .ToList();

        Assert.That(breaks, Is.Empty, $"These break workers on versions the server still accepts. Make the change compatible, or, if it has to break, raise WorkerProtocol.MinSupportedVersion to {WorkerProtocol.Version} (a breaking deploy: drain first).");
    }

    [Test]
    public void ShapeFiles_ExistOnlyForSupportedVersions()
    {
        var versions = RecordedVersions();
        var missing = Enumerable.Range(WorkerProtocol.MinSupportedVersion, WorkerProtocol.Version - WorkerProtocol.MinSupportedVersion + 1).Except(versions).ToList();
        var retired = versions.Where(v => v < WorkerProtocol.MinSupportedVersion || v > WorkerProtocol.Version).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(missing, Is.Empty, "supported versions with no recorded shape");
            Assert.That(retired, Is.Empty, "recorded shapes for versions outside the supported range: delete them");
        });
    }

    // A version below the current one has been bumped past, which happens only once it has shipped: sealing it is what keeps its record from being rewritten.
    [Test]
    public void EverySupportedVersionBelowTheCurrentOne_IsSealed()
    {
        var unsealed = Enumerable.Range(WorkerProtocol.MinSupportedVersion, WorkerProtocol.Version - WorkerProtocol.MinSupportedVersion)
            .Where(v => !WorkerProtocol.Sealed.ContainsKey(v))
            .Select(v => $"v{v}")
            .ToList();

        Assert.That(unsealed, Is.Empty, "add each one's hash (WorkerProtocol.HashOf its shape file) to WorkerProtocol.Sealed");
    }

    [Test]
    public void SealedVersions_KeepTheShapeTheyShippedWith()
    {
        Assert.Multiple(() =>
        {
            foreach (var (version, hash) in WorkerProtocol.Sealed)
            {
                var path = ShapePath(version);
                if (!File.Exists(path))
                {
                    // Retired along with its file; ShapeFiles_ExistOnlyForSupportedVersions says whether it should have been.
                    continue;
                }
                Assert.That(WorkerProtocol.HashOf(File.ReadAllText(path)), Is.EqualTo(hash), $"v{version} has shipped, so its recorded shape must not change; restore it, and record the change under a new version");
            }
        });
    }

    [Test]
    public void TheShape_HasOnlyTypesThatCarrySomething()
    {
        var lines = WorkerProtocol.ShapeText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var owners = lines.Where(l => l.StartsWith("property ") || l.StartsWith("member ")).Select(l => l.Split(' ')[1]).Select(m => m[..m.LastIndexOf('.')]).ToHashSet();
        var empty = lines.Where(l => l.StartsWith("type ")).Select(l => l.Split(' ')[1]).Where(t => !owners.Contains(t)).ToList();

        Assert.That(empty, Is.Empty, "these types reached the shape but carry nothing, so they aren't on the wire");
    }

    private static List<int> RecordedVersions()
    {
        return Directory.GetFiles(ShapeDirectory(), "v*.txt")
            .Select(f => Regex.Match(Path.GetFileName(f), @"^v(\d+)\.txt$"))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();
    }

    private static string ShapePath(int version)
    {
        return Path.Combine(ShapeDirectory(), $"v{version}.txt");
    }

    // The shapes are read and written in the source tree, not the build output: writing one is how a version gets recorded.
    private static string ShapeDirectory([CallerFilePath] string here = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "src", "Ilmarinen.Protocol", "WorkerShapes"));
    }
}
