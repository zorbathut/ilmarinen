using Ilmarinen.Server;
using NUnit.Framework;
using System.Linq;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Pins the NDJSON log-chunk format that both the log viewer and the log download read.
/// </summary>
[TestFixture]
public class LogChunkParserTests
{
    [Test]
    public void ParseEntries_MultipleLines_ReturnsEntriesInOrder()
    {
        var content = "{\"t\":\"m\",\"d\":\"=== step build ===\\n\",\"ts\":1}\n{\"t\":\"o\",\"d\":\"building\\n\",\"ts\":2}\n{\"t\":\"e\",\"d\":\"warning: unused\\n\",\"ts\":3}\n";

        var entries = LogChunkParser.ParseEntries(content).ToList();

        Assert.That(entries.Select(e => e.Type), Is.EqualTo(new[] { "m", "o", "e" }));
        Assert.That(entries.Select(e => e.Data), Is.EqualTo(new[] { "=== step build ===\n", "building\n", "warning: unused\n" }));
    }

    [Test]
    public void ParseEntries_EmbeddedNewlines_StayInOneEntry()
    {
        var content = "{\"t\":\"o\",\"d\":\"line one\\nline two\\n\",\"ts\":1}\n";

        var entries = LogChunkParser.ParseEntries(content).ToList();

        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Data, Is.EqualTo("line one\nline two\n"));
    }

    [Test]
    public void ParseEntries_WindowsLineEndings_AreParsed()
    {
        var content = "{\"t\":\"o\",\"d\":\"a\"}\r\n{\"t\":\"o\",\"d\":\"b\"}\r\n";

        var entries = LogChunkParser.ParseEntries(content).ToList();

        Assert.That(entries.Select(e => e.Data), Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void ParseEntries_UnknownTypeOrMissingData_YieldsNothing()
    {
        var content = "{\"t\":\"z\",\"d\":\"some future type\"}\n{\"t\":\"x\",\"c\":0}\n{\"d\":\"no type at all\"}\n";

        Assert.That(LogChunkParser.ParseEntries(content), Is.Empty);
    }

    [Test]
    public void ParseEntries_MalformedLines_AreSkippedWithoutThrowing()
    {
        var content = "not json at all\n123\n{\"t\":\"o\",\"d\":\"kept\"}\n{\"t\":\"o\",\"d\":\n";

        var entries = LogChunkParser.ParseEntries(content).ToList();

        Assert.That(entries.Select(e => e.Data), Is.EqualTo(new[] { "kept" }));
    }

    [Test]
    public void ParseEntries_EmptyContent_YieldsNothing()
    {
        Assert.That(LogChunkParser.ParseEntries(""), Is.Empty);
    }
}
