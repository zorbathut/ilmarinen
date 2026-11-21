using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Linq;

namespace Ilmarinen.Core.Tests.Protocol;

[TestFixture]
public class WorkerShapeDiffTests
{
    // A record and an enum, travelling the given way, with the hub methods that carry them.
    private static string Shape(string direction, params string[] lines)
    {
        var methods = direction switch
        {
            "up" => "method hub Report(T.Message, T.Kind) -> T.Answer\n",
            "down" => "method client Take(T.Message, T.Kind)\n",
            _ => "method hub Report(T.Message, T.Kind) -> T.Message\n"
        };
        return methods + $"type T.Message {direction}\ntype T.Kind {direction}\n" + string.Join("\n", lines) + "\n";
    }

    private static readonly string[] Base =
    [
        "property T.Message.Name System.String required",
        "property T.Message.Note System.String?",
        "property T.Message.Count System.Int32",
        "member T.Kind.First 0",
        "member T.Kind.Second 1"
    ];

    private static bool Breaks(string direction, string[] before, string[] after)
    {
        return WorkerShapeDiff.Breaks(Shape(direction, before), Shape(direction, after)).Count > 0;
    }

    private static string[] With(params string[] extra)
    {
        return [.. Base, .. extra];
    }

    private static string[] Without(string line)
    {
        return Base.Where(l => l != line).ToArray();
    }

    private static string[] Replacing(string line, string with)
    {
        return Base.Select(l => l == line ? with : l).ToArray();
    }

    [TestCase("up", false)]
    [TestCase("down", false)]
    [TestCase("both", false)]
    public void TheSameShape_DoesNotBreak(string direction, bool breaks)
    {
        Assert.That(Breaks(direction, Base, Base), Is.EqualTo(breaks));
    }

    [TestCase("up", false)]
    [TestCase("down", false)]
    [TestCase("both", false)]
    public void ANewOptionalNullableMember_DoesNotBreak(string direction, bool breaks)
    {
        Assert.That(Breaks(direction, Base, With("property T.Message.Extra System.Int64?")), Is.EqualTo(breaks));
    }

    [TestCase("up", true)]
    [TestCase("down", false)]
    [TestCase("both", true)]
    public void ANewRequiredMember_BreaksOnlyWhatWorkersSend(string direction, bool breaks)
    {
        Assert.That(Breaks(direction, Base, With("property T.Message.Extra System.String? required")), Is.EqualTo(breaks));
    }

    [TestCase("up", true)]
    [TestCase("down", false)]
    [TestCase("both", true)]
    public void ANewNonNullableMember_BreaksOnlyWhatWorkersSend(string direction, bool breaks)
    {
        Assert.That(Breaks(direction, Base, With("property T.Message.Extra System.Boolean")), Is.EqualTo(breaks));
    }

    [TestCase("up", false)]
    [TestCase("down", true)]
    [TestCase("both", true)]
    public void ARemovedMember_BreaksOnlyWhatTheServerSends(string direction, bool breaks)
    {
        Assert.That(Breaks(direction, Base, Without("property T.Message.Note System.String?")), Is.EqualTo(breaks));
    }

    [TestCase("up", false)]
    [TestCase("down", true)]
    [TestCase("both", true)]
    public void ANewEnumMember_BreaksOnlyWhatTheServerSends(string direction, bool breaks)
    {
        Assert.That(Breaks(direction, Base, With("member T.Kind.Third 2")), Is.EqualTo(breaks));
    }

    [TestCase("up")]
    [TestCase("down")]
    [TestCase("both")]
    public void ARemovedEnumMember_Breaks(string direction)
    {
        Assert.That(Breaks(direction, Base, Without("member T.Kind.Second 1")), Is.True);
    }

    [TestCase("up")]
    [TestCase("down")]
    [TestCase("both")]
    public void ARenumberedEnumMember_Breaks(string direction)
    {
        Assert.That(Breaks(direction, Base, Replacing("member T.Kind.Second 1", "member T.Kind.Second 5")), Is.True);
    }

    [TestCase("up")]
    [TestCase("down")]
    [TestCase("both")]
    public void AChangedMemberType_Breaks(string direction)
    {
        Assert.That(Breaks(direction, Base, Replacing("property T.Message.Count System.Int32", "property T.Message.Count System.Int64")), Is.True);
    }

    [TestCase("up", true)]
    [TestCase("down", false)]
    [TestCase("both", true)]
    public void ANullableMemberBecomingNonNull_BreaksOnlyWhatWorkersSend(string direction, bool breaks)
    {
        Assert.That(Breaks(direction, Base, Replacing("property T.Message.Note System.String?", "property T.Message.Note System.String")), Is.EqualTo(breaks));
    }

    [TestCase("up", false)]
    [TestCase("down", true)]
    [TestCase("both", true)]
    public void ANonNullMemberBecomingNullable_BreaksOnlyWhatTheServerSends(string direction, bool breaks)
    {
        Assert.That(Breaks(direction, Base, Replacing("property T.Message.Count System.Int32", "property T.Message.Count System.Int32?")), Is.EqualTo(breaks));
    }

    [TestCase("up", true)]
    [TestCase("down", false)]
    public void AMemberBecomingRequired_BreaksOnlyWhatWorkersSend(string direction, bool breaks)
    {
        Assert.That(Breaks(direction, Base, Replacing("property T.Message.Note System.String?", "property T.Message.Note System.String? required")), Is.EqualTo(breaks));
    }

    [Test]
    public void ATypeNowAlsoSentByTheServer_IsJudgedBothWays()
    {
        var before = Shape("up", Base);
        var after = Shape("both", Without("property T.Message.Note System.String?"));

        Assert.That(WorkerShapeDiff.Breaks(before, after), Is.Not.Empty);
    }

    [Test]
    public void ARemovedOrChangedMethod_Breaks()
    {
        var before = "method hub Report(T.Message) -> T.Answer\nmethod hub Ping() -> T.Answer\n";

        Assert.Multiple(() =>
        {
            Assert.That(WorkerShapeDiff.Breaks(before, "method hub Report(T.Message) -> T.Answer\n"), Is.Not.Empty, "removed");
            Assert.That(WorkerShapeDiff.Breaks(before, "method hub Report(T.Message, System.String) -> T.Answer\nmethod hub Ping() -> T.Answer\n"), Is.Not.Empty, "parameters changed");
            Assert.That(WorkerShapeDiff.Breaks(before, "method hub Report(T.Message) -> T.Other\nmethod hub Ping() -> T.Answer\n"), Is.Not.Empty, "result changed");
        });
    }

    [Test]
    public void AnAddedMethod_DoesNotBreak()
    {
        var before = "method hub Report(T.Message) -> T.Answer\n";

        Assert.Multiple(() =>
        {
            Assert.That(WorkerShapeDiff.Breaks(before, before + "method hub Ping() -> T.Answer\n"), Is.Empty, "hub");
            Assert.That(WorkerShapeDiff.Breaks(before, before + "method client Poke()\n"), Is.Empty, "client");
        });
    }

    [Test]
    public void ATypeNoLongerReached_IsJudgedByWhatReferredToIt()
    {
        var before = "type T.Message up\nproperty T.Message.Detail T.Detail?\ntype T.Detail up\nproperty T.Detail.Text System.String\n";
        var after = "type T.Message up\n";

        Assert.That(WorkerShapeDiff.Breaks(before, after), Is.Empty, "dropping a member workers send is fine, and its type with it");
    }

    [Test]
    public void AnEnumNoLongerReached_IsJudgedByWhatReferredToIt()
    {
        var before = "type T.Message up\nproperty T.Message.Kind T.Kind?\ntype T.Kind up\nmember T.Kind.First 0\n";
        var after = "type T.Message up\n";

        Assert.That(WorkerShapeDiff.Breaks(before, after), Is.Empty, "its members weren't removed: the enum just isn't sent any more");
    }

    [Test]
    public void TheRealShape_DoesNotBreakItself()
    {
        Assert.That(WorkerShapeDiff.Breaks(WorkerProtocol.ShapeText, WorkerProtocol.ShapeText), Is.Empty);
    }
}
