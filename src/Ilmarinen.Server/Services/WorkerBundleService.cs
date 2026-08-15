using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>
/// Serves the worker bundle (a zipped worker publish output baked into the server image) to launcher-run workers.
/// The bundle's identity is the SHA-256 of the zip; ProtocolVersion.Hash can't serve here because it only reflects DTO shapes and doesn't change on logic-only updates.
/// The hash is computed once per process, so the bundle can only "change" via a server restart — which drops every worker connection, so workers learn about updates at re-handshake time without any polling.
/// </summary>
public class WorkerBundleService
{
    // Domain separation from the auth-challenge signature format ("{workerId}:{b64}:{b64}", GUID-prefixed) so a bundle signature can never be replayed as a challenge signature or vice versa.
    private const string SignaturePrefix = "ilmarinen-worker-bundle:";

    private readonly ServerKeyService _serverKey;
    private readonly object _manifestLock = new();
    private WorkerBundleManifest? _manifest;

    public string? BundlePath { get; }
    public string? CurrentHash { get; }

    public WorkerBundleService(ServerConfig config, ServerKeyService serverKey)
    {
        _serverKey = serverKey;
        BundlePath = config.WorkerBundlePath;

        if (BundlePath == null)
        {
            return;
        }

        if (!File.Exists(BundlePath))
        {
            throw new ConfigurationException(
                $"Server__WorkerBundlePath is set to '{BundlePath}' but no such file exists. Point it at a zipped worker publish output, or unset it to disable bundle serving.");
        }

        using var stream = File.OpenRead(BundlePath);
        CurrentHash = Convert.ToHexString(SHA256.HashData(stream));
    }

    public bool IsEnabled
    {
        get { return BundlePath != null; }
    }

    /// <summary>
    /// Signing is deferred to first use (and then cached) so a server without ILMARINEN_SERVER_KEY still boots; the manifest request then surfaces the key ConfigurationException as a 503.
    /// </summary>
    public WorkerBundleManifest GetManifest()
    {
        if (CurrentHash == null)
        {
            throw new InvalidOperationException("Bundle serving is not enabled.");
        }

        lock (_manifestLock)
        {
            _manifest ??= new WorkerBundleManifest
            {
                BundleHash = CurrentHash,
                Signature = Convert.ToBase64String(_serverKey.Sign(Encoding.UTF8.GetBytes(SignaturePrefix + CurrentHash)))
            };
            return _manifest;
        }
    }
}

/// <summary>
/// FROZEN CONTRACT: the launcher is the one component that cannot auto-update, and it parses exactly these fields. Never rename or remove a field; additions require bumping FormatVersion, which forces a manual launcher update on every host. Property names are pinned with JsonPropertyName so no serializer-policy change can drift them; WorkerBundleEndpointTests pins them again from the outside.
/// </summary>
public record WorkerBundleManifest
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; } = 1;

    [JsonPropertyName("bundleHash")]
    public required string BundleHash { get; init; }

    [JsonPropertyName("signature")]
    public required string Signature { get; init; }

    // The bundle is framework-dependent; a launcher whose runtime major doesn't match can't run it and uses this field to say so loudly instead of failing obscurely.
    [JsonPropertyName("targetFramework")]
    public string TargetFramework { get; init; } = "net9.0";
}
