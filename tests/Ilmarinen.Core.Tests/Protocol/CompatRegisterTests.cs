using Ilmarinen.Protocol;
using NUnit.Framework;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Ilmarinen.Core.Tests.Protocol;

/// <summary>COMPAT.md lists code kept for older workers under the version that retires it. Once the server's minimum supported version reaches that version, no worker that needs the code can connect any more, and this test says what to delete.</summary>
[TestFixture]
public class CompatRegisterTests
{
    [Test]
    public void NoEntry_OutlivesTheVersionThatRetiresIt()
    {
        var text = File.ReadAllText(RegisterPath());

        // Headings inside the example block are illustration, not entries.
        var outsideCode = Regex.Replace(text, "```.*?```", "", RegexOptions.Singleline);
        var headings = Regex.Matches(outsideCode, @"^##\s.*$", RegexOptions.Multiline).Select(m => m.Value.Trim()).ToList();
        var versions = headings.Select(h => Regex.Match(h, @"^##\s+Until\s+v(\d+)\b")).ToList();
        var unparsed = headings.Where((h, i) => !versions[i].Success).ToList();
        var due = versions
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .Where(v => v <= WorkerProtocol.MinSupportedVersion)
            .Select(v => $"v{v}")
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(unparsed, Is.Empty, "every section of COMPAT.md is headed \"## Until vN\", or this test can't tell when it is due");
            Assert.That(due, Is.Empty, $"WorkerProtocol.MinSupportedVersion is {WorkerProtocol.MinSupportedVersion}, so no worker needing these COMPAT.md entries can connect: delete their code, their tests and the entries");
        });
    }

    private static string RegisterPath([CallerFilePath] string here = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "COMPAT.md"));
    }
}
