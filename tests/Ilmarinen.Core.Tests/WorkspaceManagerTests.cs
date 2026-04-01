using Ilmarinen.Worker.Services;
using Ilmarinen.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using System.IO;
using System.Linq;
using System;

namespace Ilmarinen.Core.Tests;

[TestFixture]
public class WorkspaceManagerTests
{
    private string _tempDir = null!;
    private WorkspaceManager _manager = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ws-manager-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = new WorkerConfig
        {
            ServerUrl = "http://localhost",
            WorkspacePath = _tempDir,
            WorkerKey = "test:01DUMMY00000000000000000000:AAAA:BBBB"
        };

        _manager = new WorkspaceManager(config, NullLogger<WorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public void DiscoverWorkspaces_ReturnsNamedDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "my-project"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "another-ws"));

        var result = _manager.DiscoverWorkspaces();
        var names = result.Select(w => w.Name).ToList();

        Assert.That(names, Is.EquivalentTo(new[] { "my-project", "another-ws" }));
        Assert.That(result, Has.All.Matches<Ilmarinen.Protocol.Responses.WorkspaceInfo>(
            w => w.Path == Path.GetFullPath(Path.Combine(_tempDir, w.Name))));
    }

    [Test]
    public void DiscoverWorkspaces_FiltersOutUlidDirectories()
    {
        // ULID-named dirs are ephemeral
        Directory.CreateDirectory(Path.Combine(_tempDir, "01HY5Z0E3BQXK5M7N2P4R6S8T0"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "01J0ABCDEF0123456789ABCDEF"));
        // Named dirs are persistent
        Directory.CreateDirectory(Path.Combine(_tempDir, "real-workspace"));

        var result = _manager.DiscoverWorkspaces();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.Select(w => w.Name), Does.Contain("real-workspace"));
    }

    [Test]
    public void DiscoverWorkspaces_EmptyDirectory_ReturnsEmpty()
    {
        var result = _manager.DiscoverWorkspaces();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void DiscoverWorkspaces_NonExistentPath_ReturnsEmpty()
    {
        Directory.Delete(_tempDir);

        var result = _manager.DiscoverWorkspaces();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void TryDelete_ActiveWorkspace_RefusesDeletion()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "active-ws"));
        _manager.SetActiveWorkspace("active-ws");

        var result = _manager.TryDelete("active-ws");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("in use"));
    }

    [Test]
    public void TryDelete_NonExistentWorkspace_ReturnsError()
    {
        var result = _manager.TryDelete("no-such-ws");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("does not exist"));
    }

    [Test]
    public void TryDelete_InvalidName_ReturnsError()
    {
        var result = _manager.TryDelete("../escape");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error, Does.Contain("Path separators"));
    }

    [Test]
    public void TryDelete_ValidWorkspace_DeletesDirectory()
    {
        var wsPath = Path.Combine(_tempDir, "deletable");
        Directory.CreateDirectory(wsPath);
        File.WriteAllText(Path.Combine(wsPath, "file.txt"), "content");

        var result = _manager.TryDelete("deletable");

        Assert.That(result.Success, Is.True);
        Assert.That(Directory.Exists(wsPath), Is.False);
    }

    [Test]
    public void TryDelete_AfterClearingActiveWorkspace_Succeeds()
    {
        var wsPath = Path.Combine(_tempDir, "was-active");
        Directory.CreateDirectory(wsPath);

        _manager.SetActiveWorkspace("was-active");
        _manager.SetActiveWorkspace(null);

        var result = _manager.TryDelete("was-active");

        Assert.That(result.Success, Is.True);
        Assert.That(Directory.Exists(wsPath), Is.False);
    }
}
