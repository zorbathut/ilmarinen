using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System;

namespace Ilmarinen.Protocol;

/// <summary>
/// Computes a deterministic hash of all protocol types (requests, responses, shared).
/// Used for compatibility checks between server, worker, and CLI — they are compatible
/// as long as the protocol shape hasn't changed, regardless of other code changes.
/// </summary>
public static class ProtocolVersion
{
    private static readonly string[] SeedNamespaces =
    [
        "Ilmarinen.Protocol.Requests",
        "Ilmarinen.Protocol.Responses",
        "Ilmarinen.Protocol.Shared"
    ];

    /// <summary>
    /// Returns the full text that gets hashed, for debugging protocol mismatches.
    /// </summary>
    public static string HashInput { get; } = ComputeHashInput();

    public static string Hash { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(HashInput)))[..12];

    private static string ComputeHashInput()
    {
        var protocolAssembly = typeof(ProtocolVersion).Assembly;

        // Start with all exported types in the seed namespaces
        var seedTypes = protocolAssembly.GetExportedTypes()
            .Where(t => SeedNamespaces.Any(ns => t.Namespace == ns));

        // Recursively collect all referenced Ilmarinen types
        var allTypes = new SortedDictionary<string, Type>();
        foreach (var type in seedTypes)
            CollectTypes(type, allTypes);

        // Hash the shape
        var sb = new StringBuilder();
        foreach (var (name, type) in allTypes)
        {
            sb.Append(name);
            if (type.IsEnum)
            {
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static)
                             .OrderBy(f => f.Name))
                {
                    sb.Append($" {field.Name}={field.GetRawConstantValue()}");
                }
            }
            else
            {
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                             .OrderBy(p => p.Name))
                {
                    sb.Append($" {prop.Name}:{FormatType(prop.PropertyType)}");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void CollectTypes(Type type, SortedDictionary<string, Type> collected)
    {
        // Unwrap nullable, arrays, generics to get the underlying types
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
            type = underlying;

        if (type.IsArray)
            type = type.GetElementType()!;

        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
                CollectTypes(arg, collected);

            // Don't collect the generic type itself (e.g., List<T>)
            if (!IsIlmarinenType(type))
                return;
        }

        // Only collect types from Ilmarinen assemblies
        if (!IsIlmarinenType(type))
            return;

        var fullName = type.FullName!;
        if (collected.ContainsKey(fullName))
            return;

        collected[fullName] = type;

        // Recurse into property types
        if (!type.IsEnum)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                CollectTypes(prop.PropertyType, collected);
        }
    }

    private static bool IsIlmarinenType(Type type) =>
        type.Assembly.GetName().Name?.StartsWith("Ilmarinen") == true;

    private static string FormatType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
            return $"{FormatType(underlying)}?";

        if (type.IsArray)
            return $"{FormatType(type.GetElementType()!)}[]";

        if (type.IsGenericType)
        {
            var name = type.GetGenericTypeDefinition().Name;
            var args = string.Join(",", type.GetGenericArguments().Select(FormatType));
            return $"{name}<{args}>";
        }

        return type.FullName ?? type.Name;
    }
}
