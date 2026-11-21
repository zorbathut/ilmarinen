using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Core.Tests.Protocol;

/// <summary>The walker that renders the worker protocol's shape, on a small protocol of its own; this assembly's name starts with "Ilmarinen", so its types count as the protocol's.</summary>
[TestFixture]
public class WorkerShapeTests
{
    public enum ShapeKind
    {
        First = 0,
        Second = 7
    }

    public record ShapeBase
    {
        public string? Inherited { get; init; }
    }

    public record ShapeUp : ShapeBase
    {
        public required string Name { get; init; }
        public List<string?> Notes { get; init; } = [];
        public Dictionary<string, long?>? Sizes { get; init; }
        public ShapeKind Kind { get; init; }
        public ShapeShared? Shared { get; init; }
    }

    public record ShapeDown
    {
        public int Count { get; init; }
        public ShapeShared Shared { get; init; } = new();
    }

    public record ShapeShared
    {
        public Guid? Id { get; init; }
    }

    public interface IShapeHub
    {
        Task<ShapeDown> Send(ShapeUp message);
        Task Ping();
        int Bare(Guid id);
    }

    public interface IShapeClient
    {
        Task Take(ShapeDown message, string? note);
    }

    public interface IShapeHttp
    {
        Task<ShapeDown> Upload(Guid id);
    }

    private static string[] Lines()
    {
        return WorkerShape.Describe(typeof(IShapeHub), typeof(IShapeClient), typeof(IShapeHttp)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Name<T>()
    {
        return typeof(T).FullName!;
    }

    [Test]
    public void Methods_ShowTheirParametersAndWhatComesBack()
    {
        var lines = Lines();

        Assert.That(lines, Does.Contain($"method hub Send({Name<ShapeUp>()}) -> {Name<ShapeDown>()}"));
        Assert.That(lines, Does.Contain("method hub Ping()"), "a bare Task brings nothing back");
        Assert.That(lines, Does.Contain("method hub Bare(System.Guid) -> System.Int32"));
        Assert.That(lines, Does.Contain($"method client Take({Name<ShapeDown>()}, System.String?)"));
        Assert.That(lines, Does.Contain($"method http Upload(System.Guid) -> {Name<ShapeDown>()}"));
    }

    [Test]
    public void Types_AreTaggedWithTheWayTheyTravel()
    {
        var lines = Lines();

        Assert.That(lines, Does.Contain($"type {Name<ShapeUp>()} up"));
        Assert.That(lines, Does.Contain($"type {Name<ShapeDown>()} down"), "a hub result, a client parameter and an HTTP result all travel down");
        Assert.That(lines, Does.Contain($"type {Name<ShapeShared>()} both"), "reached from both");
        Assert.That(lines, Does.Contain($"type {Name<ShapeKind>()} up"));
    }

    [Test]
    public void Properties_ShowNullabilityRequirednessAndInheritance()
    {
        var lines = Lines();
        var up = Name<ShapeUp>();

        Assert.That(lines, Does.Contain($"property {up}.Name System.String required"));
        Assert.That(lines, Does.Contain($"property {up}.Notes System.Collections.Generic.List<System.String?>"));
        Assert.That(lines, Does.Contain($"property {up}.Sizes System.Collections.Generic.Dictionary<System.String, System.Int64?>?"));
        Assert.That(lines, Does.Contain($"property {up}.Kind {Name<ShapeKind>()}"));
        Assert.That(lines, Does.Contain($"property {up}.Inherited System.String?"), "an inherited property is on the wire too");
        Assert.That(lines, Does.Contain($"property {Name<ShapeShared>()}.Id System.Guid?"));
    }

    [Test]
    public void EnumMembers_ShowTheirValues()
    {
        var lines = Lines();

        Assert.That(lines, Does.Contain($"member {Name<ShapeKind>()}.First 0"));
        Assert.That(lines, Does.Contain($"member {Name<ShapeKind>()}.Second 7"));
    }

    // The shape describes a type by its properties' names and types, which is what goes on the wire only while nothing renames, hides or reshapes them. A protocol type that does would need the walker taught about it first.
    [Test]
    public void TheProtocolsTypes_UseNothingTheShapeCantSee()
    {
        var types = WorkerProtocol.ShapeText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("type "))
            .Select(l => l.Split(' ')[1])
            .Select(n => typeof(WorkerProtocol).Assembly.GetType(n) ?? throw new InvalidOperationException($"no type {n}"))
            .ToList();

        var generic = types.Where(t => t.IsGenericType).Select(t => t.FullName).ToList();
        var attributed = types
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => (Type: t, Property: p)))
            .Where(x => x.Property.GetCustomAttributes().Any(a => a.GetType().Namespace == typeof(JsonIgnoreAttribute).Namespace))
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();
        var withFields = types.Where(t => !t.IsEnum && t.GetFields(BindingFlags.Public | BindingFlags.Instance).Length > 0).Select(t => t.Name).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(generic, Is.Empty, "a generic protocol type's name doesn't fit the shape's lines");
            Assert.That(attributed, Is.Empty, "serialization attributes change the wire without changing the shape");
            Assert.That(withFields, Is.Empty, "public fields aren't serialized, so one here is data that never arrives");
        });
    }
}
