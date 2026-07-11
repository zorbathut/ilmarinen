using LibGit2Sharp;
using System.Collections.Generic;
using System.IO;
using System;

namespace Ilmarinen.IntegrationTests.Fixtures;

public class TestGitRepository : IDisposable
{
    private readonly string _basePath;
    private readonly string _workTreePath;
    private readonly List<string> _addedFiles = new();

    public string Url => _workTreePath;  // LibGit2Sharp can clone from local paths directly
    public string WorkTreePath => _workTreePath;
    public string Branch { get; private set; } = "master";

    public TestGitRepository()
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"ilmarinen-test-repo-{Guid.NewGuid():N}");
        _workTreePath = Path.Combine(_basePath, "work");

        Directory.CreateDirectory(_basePath);

        // Create a non-bare repository directly (simpler than bare + clone)
        Repository.Init(_workTreePath);

        // Ensure the initial branch is "master" regardless of system git config (init.defaultBranch may be set to something else)
        using var repo = new Repository(_workTreePath);
        repo.Refs.UpdateTarget("HEAD", "refs/heads/master");
    }

    public void AddFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_workTreePath, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(fullPath, content);
        _addedFiles.Add(relativePath);
    }

    public string Commit(string message)
    {
        using var repo = new Repository(_workTreePath);

        // Stage all added files explicitly
        foreach (var file in _addedFiles)
        {
            Commands.Stage(repo, file);
        }

        var author = new Signature("Test", "test@example.com", DateTimeOffset.Now);
        var commit = repo.Commit(message, author, author);

        // Track the branch name (first commit creates HEAD)
        Branch = repo.Head.FriendlyName;

        _addedFiles.Clear();
        return commit.Sha;
    }

    public void Dispose()
    {
        try
        {
            // Git files may be read-only
            SetAttributesNormal(new DirectoryInfo(_basePath));
            Directory.Delete(_basePath, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private static void SetAttributesNormal(DirectoryInfo dir)
    {
        foreach (var subDir in dir.GetDirectories())
        {
            SetAttributesNormal(subDir);
        }

        foreach (var file in dir.GetFiles())
        {
            file.Attributes = FileAttributes.Normal;
        }
    }
}
