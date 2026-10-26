using Ilmarinen.Scripting;
using Ilmarinen.Worker.Services;
using LibGit2Sharp;
using Microsoft.CodeAnalysis.Scripting;
using NUnit.Framework;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;
using static Ilmarinen.Core.Tests.GitTestRepos;

namespace Ilmarinen.Core.Tests;

/// <summary>
/// A pipeline script compiled straight from a commit sees the same files through <c>#load</c> that it would in a checkout of that commit.
/// </summary>
[TestFixture]
public class GitTreeSourceResolverTests
{
    private const string StepScript = """Step("s").Image("alpine").Run(async ctx => { });""";

    private string _repoDir = null!;
    private Repository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), $"tree-resolver-test-{Guid.NewGuid():N}");
        _repo = new Repository(Repository.Init(_repoDir, isBare: true));
    }

    [TearDown]
    public void TearDown()
    {
        _repo.Dispose();
        ForceDeleteDirectory(_repoDir);
    }

    [Test]
    public async Task Load_SiblingParentAndNestedLoads_ResolveAgainstTheIncludingFile()
    {
        var commit = Commit(new()
        {
            ["build/pipeline.csx"] = "#load \"common.csx\"\n#load \"../shared/root.csx\"\nWorkspace(Common + Root + Nested);\n" + StepScript,
            ["build/common.csx"] = "#load \"lib/nested.csx\"\nvar Common = \"c-\";\n",
            ["build/lib/nested.csx"] = "#load \"sibling.csx\"\nvar Nested = Sibling;\n",
            ["build/lib/sibling.csx"] = "var Sibling = \"n\";\n",
            ["shared/root.csx"] = "var Root = \"r-\";\n",
        });

        var result = await LoadAsync(commit, "build/pipeline.csx");

        Assert.That(result.Workspace?.Name, Is.EqualTo("c-r-n"));
        Assert.That(result.Steps, Has.Count.EqualTo(1));
    }

    [TestCase("./build/pipeline.csx")]
    [TestCase("build//pipeline.csx")]
    [TestCase("build/./pipeline.csx")]
    public async Task Load_ScriptPathSpellings(string scriptPath)
    {
        var commit = Commit(new() { ["build/pipeline.csx"] = StepScript });

        var result = await LoadAsync(commit, scriptPath);

        Assert.That(result.Steps, Has.Count.EqualTo(1));
    }

    [TestCase("/etc/passwd")]
    [TestCase("../outside.csx")]
    [TestCase("build/../../outside.csx")]
    public void FilePathFor_PathOutsideTheRepository_IsNull(string scriptPath)
    {
        var resolver = new GitTreeSourceResolver(Commit(new() { ["build/pipeline.csx"] = StepScript }));

        Assert.That(resolver.FilePathFor(scriptPath), Is.Null);
    }

    [Test]
    public void FilePathFor_MissingScript_DoesNotExist()
    {
        var resolver = new GitTreeSourceResolver(Commit(new() { ["build/pipeline.csx"] = StepScript }));

        Assert.That(resolver.Exists(resolver.FilePathFor("build/other.csx")!), Is.False);
    }

    [TestCase("missing.csx")]
    [TestCase("../../escape.csx")]
    [TestCase("/etc/hostname")]
    public void Load_UnresolvableLoad_FailsNamingThePath(string loadPath)
    {
        var commit = Commit(new() { ["build/pipeline.csx"] = $"#load \"{loadPath}\"\n" + StepScript });

        var ex = Assert.ThrowsAsync<CompilationErrorException>(() => LoadAsync(commit, "build/pipeline.csx"));
        Assert.That(ex!.Message, Does.Contain(loadPath));
    }

    [Test]
    public async Task Load_SymlinkedScriptsAndLoads_AreFollowedAsInACheckout()
    {
        var commit = Commit(
            new() { ["build/real.csx"] = "#load \"common.csx\"\n" + StepScript, ["shared/common-real.csx"] = "var X = 1;\n" },
            symlinks: new() { ["build/pipeline.csx"] = "real.csx", ["build/common.csx"] = "../shared/link.csx", ["shared/link.csx"] = "common-real.csx" });

        var result = await LoadAsync(commit, "build/pipeline.csx");

        Assert.That(result.Steps, Has.Count.EqualTo(1));
    }

    [TestCase("../../outside.csx")]
    [TestCase("/etc/hostname")]
    [TestCase("loop.csx")]
    public void Load_SymlinkLeavingTheRepositoryOrLooping_FailsNamingTheLink(string target)
    {
        var commit = Commit(new() { ["build/pipeline.csx"] = "#load \"link.csx\"\n" + StepScript }, symlinks: new() { ["build/link.csx"] = target, ["build/loop.csx"] = "link.csx" });

        var ex = Assert.ThrowsAsync<CompilationErrorException>(() => LoadAsync(commit, "build/pipeline.csx"));
        Assert.That(ex!.Message, Does.Contain("link.csx"));
    }

    [Test]
    public void Load_DirectoryLoad_Fails()
    {
        var commit = Commit(new() { ["build/pipeline.csx"] = "#load \"lib\"\n" + StepScript, ["build/lib/x.csx"] = "var X = 1;\n" });

        Assert.ThrowsAsync<CompilationErrorException>(() => LoadAsync(commit, "build/pipeline.csx"));
    }

    [Test]
    public void Load_CompileError_NamesTheRepositoryPath()
    {
        var commit = Commit(new() { ["build/pipeline.csx"] = "#load \"common.csx\"\n" + StepScript, ["build/common.csx"] = "this is not C#\n" });

        var ex = Assert.ThrowsAsync<CompilationErrorException>(() => LoadAsync(commit, "build/pipeline.csx"));
        Assert.That(ex!.Message, Does.Contain("build/common.csx"));
    }

    private static async Task<PipelineScriptResult> LoadAsync(Commit commit, string scriptPath)
    {
        var resolver = new GitTreeSourceResolver(commit);
        return await PipelineScript.LoadAsync(resolver.FilePathFor(scriptPath)!, resolver);
    }

    private Commit Commit(Dictionary<string, string> files, Dictionary<string, string>? symlinks = null)
    {
        var tree = new TreeDefinition();
        foreach (var (path, content) in files)
        {
            tree.Add(path, Blob(content), Mode.NonExecutableFile);
        }

        foreach (var (path, target) in symlinks ?? [])
        {
            tree.Add(path, Blob(target), Mode.SymbolicLink);
        }

        var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
        return _repo.ObjectDatabase.CreateCommit(signature, signature, "test", _repo.ObjectDatabase.CreateTree(tree), Enumerable.Empty<Commit>(), prettifyMessage: false);
    }

    private Blob Blob(string content)
    {
        return _repo.ObjectDatabase.CreateBlob(new MemoryStream(Encoding.UTF8.GetBytes(content)));
    }
}
