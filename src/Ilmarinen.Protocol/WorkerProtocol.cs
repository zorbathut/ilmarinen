using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Protocol;

/// <summary>
/// The worker protocol's version. The server accepts a worker whose <see cref="Version"/> lies from <see cref="MinSupportedVersion"/> to its own, so a change that older workers can still speak deploys without stranding them, and only a breaking one, which raises the minimum, needs the fleet drained first. CLAUDE.md ("Protocol compatibility") has the rules for changing it.
///
/// <see cref="ShapeText"/> is what the protocol looks like on the wire, recorded per version under WorkerShapes/ and checked by the test suite (WorkerShapeDiff there has the rules), so a change that doesn't bump the version, or that breaks a version still supported, fails there rather than in the fleet.
/// </summary>
public static class WorkerProtocol
{
    public const int Version = 2;
    public const int MinSupportedVersion = 1;

    /// <summary>The hashes of versions that have shipped, so their recorded shapes can't be amended after the fact. Every supported version below the current one must be sealed; the current one is sealed once it ships.</summary>
    public static readonly IReadOnlyDictionary<int, string> Sealed = new Dictionary<int, string>
    {
        [1] = "7042CD2E0F42"
    };

    public static string ShapeText { get; } = WorkerShape.Describe(typeof(IWorkerHub), typeof(IWorkerClient), typeof(IWorkerHttp));

    /// <summary>A short hash of <see cref="ShapeText"/>. A worker sends it with its version, and the server logs one that differs from what it knows of that version: a version's shape changed after one of the two was built.</summary>
    public static string Hash { get; } = HashOf(ShapeText);

    /// <summary>The hash of a shape. Line endings are normalized, so a checkout that rewrites them doesn't change a sealed version's hash.</summary>
    public static string HashOf(string shapeText)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(shapeText.Replace("\r\n", "\n"))))[..12];
    }

    /// <summary>The hash this server knows for a version it accepts: its own, or the sealed one; null for a version that shipped unsealed.</summary>
    public static string? KnownHash(int version)
    {
        return version == Version ? Hash : Sealed.GetValueOrDefault(version);
    }
}

/// <summary>Which way a type travels: worker→server ("up"), server→worker ("down"), or both. What a peer on an older version tolerates depends on it.</summary>
[Flags]
public enum WorkerShapeDirection
{
    Up = 1,
    Down = 2
}

/// <summary>
/// Renders the worker protocol's shape as text, one fact per line, sorted:
/// <code>
/// method hub Connect(Ilmarinen.Protocol.Requests.WorkerConnect) -> Ilmarinen.Protocol.Responses.AuthChallenge
/// type Ilmarinen.Protocol.Requests.WorkerConnect up
/// property Ilmarinen.Protocol.Requests.WorkerConnect.BundleHash System.String?
/// property Ilmarinen.Protocol.Requests.WorkerConnect.Nonce System.Byte[] required
/// member Ilmarinen.Protocol.JobStatus.Queued 1
/// </code>
/// Hub and HTTP methods' parameters travel up and their results down; client methods' parameters travel down. Every Ilmarinen type reachable from them is included.
///
/// Strings carrying structured content of their own are just strings here, and their content is a contract of its own: the metrics NDJSON in a JobMetricsChunk versions its records itself (MetricsRecords), and the log NDJSON in a LogChunk keeps its keys and only adds line types, which readers skip when they don't know them (CLAUDE.md).
/// </summary>
public static class WorkerShape
{
    public static string Describe(Type hub, Type client, Type http)
    {
        // Not thread-safe, so one per call.
        var nullability = new NullabilityInfoContext();
        var lines = new SortedSet<string>(StringComparer.Ordinal);
        var directions = new Dictionary<Type, WorkerShapeDirection>();

        var methods = hub.GetMethods().Select(m => (m, "hub"))
            .Concat(client.GetMethods().Select(m => (m, "client")))
            .Concat(http.GetMethods().Select(m => (m, "http")));
        foreach (var (method, kind) in methods)
        {
            var parameters = method.GetParameters();
            var outward = kind == "client" ? WorkerShapeDirection.Down : WorkerShapeDirection.Up;
            var signature = $"method {kind} {method.Name}({string.Join(", ", parameters.Select(p => Format(p.ParameterType, nullability.Create(p))))})";
            foreach (var parameter in parameters)
            {
                Collect(parameter.ParameterType, outward, directions);
            }

            var (result, resultNullability) = Unwrap(method.ReturnParameter, nullability);
            if (result != null)
            {
                signature += $" -> {Format(result, resultNullability!)}";
                Collect(result, WorkerShapeDirection.Down, directions);
            }
            lines.Add(signature);
        }

        foreach (var (type, direction) in directions)
        {
            lines.Add($"type {type.FullName} {DirectionName(direction)}");
            if (type.IsEnum)
            {
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    lines.Add($"member {type.FullName}.{field.Name} {Convert.ToInt64(field.GetRawConstantValue())}");
                }
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var required = property.GetCustomAttribute<RequiredMemberAttribute>() != null ? " required" : "";
                lines.Add($"property {type.FullName}.{property.Name} {Format(property.PropertyType, nullability.Create(property))}{required}");
            }
        }

        return string.Join("\n", lines) + "\n";
    }

    public static string DirectionName(WorkerShapeDirection direction)
    {
        return direction switch
        {
            WorkerShapeDirection.Up => "up",
            WorkerShapeDirection.Down => "down",
            _ => "both"
        };
    }

    // A Task's result is what goes on the wire; a bare Task carries nothing back.
    private static (Type? Type, NullabilityInfo? Nullability) Unwrap(ParameterInfo returned, NullabilityInfoContext nullability)
    {
        var info = nullability.Create(returned);
        if (returned.ParameterType == typeof(Task) || returned.ParameterType == typeof(void))
        {
            return (null, null);
        }
        if (returned.ParameterType.IsGenericType && returned.ParameterType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            return (returned.ParameterType.GetGenericArguments()[0], info.GenericTypeArguments[0]);
        }
        return (returned.ParameterType, info);
    }

    private static void Collect(Type type, WorkerShapeDirection direction, Dictionary<Type, WorkerShapeDirection> directions)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsArray)
        {
            Collect(type.GetElementType()!, direction, directions);
            return;
        }
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                Collect(argument, direction, directions);
            }
        }
        if (!IsIlmarinenType(type))
        {
            return;
        }

        var known = directions.GetValueOrDefault(type);
        if ((known & direction) == direction)
        {
            return;
        }
        directions[type] = known | direction;

        if (!type.IsEnum)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Collect(property.PropertyType, direction, directions);
            }
        }
    }

    private static bool IsIlmarinenType(Type type)
    {
        return type.Assembly.GetName().Name?.StartsWith("Ilmarinen") == true;
    }

    private static string Format(Type type, NullabilityInfo info)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            return $"{Format(underlying, info)}?";
        }

        var nullable = !type.IsValueType && info.ReadState == NullabilityState.Nullable ? "?" : "";
        if (type.IsArray)
        {
            return $"{Format(type.GetElementType()!, info.ElementType!)}[]{nullable}";
        }
        if (type.IsGenericType)
        {
            var name = type.GetGenericTypeDefinition().FullName!;
            name = name[..name.IndexOf('`')];
            var arguments = type.GetGenericArguments().Select((a, i) => Format(a, info.GenericTypeArguments[i]));
            return $"{name}<{string.Join(", ", arguments)}>{nullable}";
        }
        return $"{type.FullName}{nullable}";
    }
}
