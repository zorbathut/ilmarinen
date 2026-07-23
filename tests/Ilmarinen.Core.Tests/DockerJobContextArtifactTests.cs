using Ilmarinen.Docker;
using Ilmarinen.Models;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Ilmarinen.Core.Tests;

[TestFixture]
public class DockerJobContextArtifactTests
{
    private string _localWorkDir = null!;
    private string _hostWorkDir = null!;
    private string? _savedPath;
    private string? _savedName;

    [SetUp]
    public void SetUp()
    {
        _localWorkDir = Path.Combine(Path.GetTempPath(), $"artifact-test-local-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_localWorkDir);

        // Deliberately nonexistent, mirroring a containerized worker where the daemon-side volume path (/var/lib/docker/volumes/...) is not visible on the worker's own filesystem
        _hostWorkDir = Path.Combine(Path.GetTempPath(), $"artifact-test-host-{Guid.NewGuid():N}");

        _savedPath = null;
        _savedName = null;
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_localWorkDir))
        {
            Directory.Delete(_localWorkDir, recursive: true);
        }
    }

    private DockerJobContext CreateContext()
    {
        // SaveArtifact never touches the Docker client or container, so dummies suffice
        return new DockerJobContext(
            client: null!,
            containerId: "unused",
            containerWorkDir: "/workspace",
            localWorkDir: _localWorkDir,
            hostWorkDir: _hostWorkDir,
            networkName: "unused",
            branch: "main",
            commit: "0000000000000000000000000000000000000000",
            secretProvider: _ => null,
            artifactSaver: (path, name) =>
            {
                _savedPath = path;
                _savedName = name;
                return Task.FromResult(new ArtifactRef { Name = name ?? Path.GetFileName(path), Size = new FileInfo(path).Length });
            });
    }

    [Test]
    public async Task SaveArtifact_WorkspacePath_ResolvesAgainstLocalWorkDir()
    {
        var filePath = Path.Combine(_localWorkDir, "deploy", "output.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "zip contents");

        var context = CreateContext();
        var result = await context.SaveArtifact("/workspace/deploy/output.zip", "my-artifact");

        Assert.That(_savedPath, Is.EqualTo(filePath));
        Assert.That(_savedName, Is.EqualTo("my-artifact"));
        Assert.That(result.Name, Is.EqualTo("my-artifact"));
    }

    [Test]
    public async Task SaveArtifact_RelativePath_ResolvesAgainstLocalWorkDir()
    {
        var filePath = Path.Combine(_localWorkDir, "output.txt");
        await File.WriteAllTextAsync(filePath, "hello");

        var context = CreateContext();
        await context.SaveArtifact("output.txt");

        Assert.That(_savedPath, Is.EqualTo(filePath));
        Assert.That(_savedName, Is.Null);
    }

    [Test]
    public void SaveArtifact_AbsolutePathOutsideWorkspace_Throws()
    {
        var context = CreateContext();

        Assert.ThrowsAsync<ArgumentException>(() => context.SaveArtifact("/etc/passwd"));
    }

    [Test]
    public void SaveArtifact_MissingFile_ThrowsFileNotFound()
    {
        var context = CreateContext();

        var ex = Assert.ThrowsAsync<FileNotFoundException>(() => context.SaveArtifact("does-not-exist.txt"));
        Assert.That(ex!.FileName, Is.EqualTo(Path.Combine(_localWorkDir, "does-not-exist.txt")));
    }
}
