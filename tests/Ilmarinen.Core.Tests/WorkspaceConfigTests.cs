using Ilmarinen.Models;
using NUnit.Framework;
using System;

namespace Ilmarinen.Core.Tests;

[TestFixture]
public class WorkspaceConfigTests
{
    [TestCase("myworkspace")]
    [TestCase("my-workspace")]
    [TestCase("my_workspace")]
    [TestCase("my.workspace")]
    [TestCase("MyWorkspace123")]
    [TestCase("a")]
    [TestCase("workspace-with-many-dashes")]
    public void IsValidName_ValidNames_ReturnsTrue(string name)
    {
        var result = WorkspaceConfig.IsValidName(name, out var error);

        Assert.That(result, Is.True);
        Assert.That(error, Is.Null);
    }

    [TestCase("../escape", "Path separators")]
    [TestCase("foo/../bar", "Path separators")]
    [TestCase("..hidden", "Path separators")]
    [TestCase("a/b", "Path separators")]
    [TestCase("a\\b", "Path separators")]
    public void IsValidName_PathTraversal_ReturnsFalse(string name, string expectedErrorContains)
    {
        var result = WorkspaceConfig.IsValidName(name, out var error);

        Assert.That(result, Is.False);
        Assert.That(error, Does.Contain(expectedErrorContains));
    }

    [TestCase("/etc/passwd")]
    [TestCase("/tmp/workspace")]
    public void IsValidName_AbsolutePath_ReturnsFalse(string name)
    {
        var result = WorkspaceConfig.IsValidName(name, out var error);

        Assert.That(result, Is.False);
        Assert.That(error, Does.Contain("absolute path").Or.Contain("Path separators"));
    }

    [TestCase(".hidden")]
    [TestCase(".git")]
    [TestCase("..")]
    public void IsValidName_LeadingDot_ReturnsFalse(string name)
    {
        var result = WorkspaceConfig.IsValidName(name, out var error);

        Assert.That(result, Is.False);
    }

    [TestCase("work space", " ")]
    [TestCase("work@space", "@")]
    [TestCase("work#space", "#")]
    [TestCase("work$space", "$")]
    [TestCase("work!space", "!")]
    [TestCase("work%space", "%")]
    [TestCase("work&space", "&")]
    [TestCase("work*space", "*")]
    [TestCase("work=space", "=")]
    [TestCase("work+space", "+")]
    public void IsValidName_InvalidCharacters_ReturnsFalse(string name, string invalidChar)
    {
        var result = WorkspaceConfig.IsValidName(name, out var error);

        Assert.That(result, Is.False);
        Assert.That(error, Does.Contain($"invalid character '{invalidChar}'"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void IsValidName_NullOrEmpty_ReturnsFalse(string? name)
    {
        var result = WorkspaceConfig.IsValidName(name!, out var error);

        Assert.That(result, Is.False);
        Assert.That(error, Does.Contain("null or empty"));
    }

    [Test]
    public void ValidateName_ValidName_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => WorkspaceConfig.ValidateName("valid-name"));
    }

    [Test]
    public void ValidateName_InvalidName_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => WorkspaceConfig.ValidateName("../escape"));

        Assert.That(ex!.Message, Does.Contain("Path separators"));
    }
}
