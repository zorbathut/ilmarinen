using System.Text.Json;
using System;

namespace Ilmarinen.WorkerLauncher;

/// <summary>
/// The server's bundle manifest. FROZEN CONTRACT — this mirrors WorkerBundleService's manifest record on the server, parsed by hand so no serializer-policy drift can break a deployed launcher. Unknown fields are ignored; a formatVersion other than 1 means this launcher is too old and must be updated manually.
/// </summary>
public record BundleManifest
{
    public required string BundleHash { get; init; }
    public required string Signature { get; init; }
    public required string TargetFramework { get; init; }

    public static BundleManifest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("formatVersion", out var formatVersion) || formatVersion.GetInt32() != 1)
        {
            throw new FormatException(
                "Unsupported bundle manifest format. This launcher is too old for the server and must be updated manually on this host.");
        }

        return new BundleManifest
        {
            BundleHash = GetRequiredString(root, "bundleHash"),
            Signature = GetRequiredString(root, "signature"),
            TargetFramework = GetRequiredString(root, "targetFramework")
        };
    }

    private static string GetRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"Bundle manifest is missing required field '{name}'.");
        }
        return value.GetString()!;
    }
}
