using System.IO;
using System.Security.Cryptography;
using System;

namespace Ilmarinen.Worker;

public class WorkerConfig
{
    /// <summary>
    /// SignalR hub URL (worker port, typically 8081).
    /// </summary>
    public required string ServerUrl { get; init; }

    /// <summary>
    /// Combined worker key: {name}:{ulidBase64}:{workerPrivBase64}:{serverPubBase64}
    /// Set via ILMARINEN_WORKER_KEY environment variable.
    /// The name is for human readability; the ULID is the actual identity.
    /// </summary>
    public required string WorkerKey { get; init; }
    public string WorkspacePath { get; init; } = GetDefaultWorkspacePath();

    public static string GetDefaultWorkspacePath()
    {
        var dataDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(dataDir))
            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(dataDir, "ilmarinen", "workspaces");
    }


    /// <summary>
    /// Host-side path for Docker bind mounts. Auto-discovered at startup
    /// when running in Docker. Falls back to WorkspacePath when not containerized.
    /// </summary>
    public string HostWorkspacePath { get; set; } = "";

    /// <summary>
    /// Container ID of the worker when running in Docker-in-Docker.
    /// Used to connect the worker to job networks for AgentApiServer communication.
    /// Null when running outside of Docker.
    /// </summary>
    public string? WorkerContainerId { get; set; }

    public string GetHostWorkspacePath() =>
        string.IsNullOrEmpty(HostWorkspacePath) ? WorkspacePath : HostWorkspacePath;

    public Guid GetWorkerId()
    {
        var parts = GetKeyParts();
        // New keys use standard Guid format; legacy keys use 26-char Crockford Base32 (Ulid)
        if (Guid.TryParse(parts.id, out var guid))
            return guid;
        return UlidStringToGuid(parts.id);
    }

    /// <summary>
    /// Converts a 26-char Crockford Base32 Ulid string to a Guid, for legacy worker keys.
    /// </summary>
    private static Guid UlidStringToGuid(string ulidString)
    {
        if (ulidString.Length != 26)
            throw new FormatException($"Invalid worker ID format: '{ulidString}'. Expected a GUID or 26-char ULID.");

        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var upper = ulidString.ToUpperInvariant();
        var bytes = new byte[16];
        var bits = new System.Collections.BitArray(130);
        for (var i = 0; i < 26; i++)
        {
            var val = alphabet.IndexOf(upper[i]);
            if (val < 0)
                throw new FormatException($"Invalid character '{ulidString[i]}' in ULID string.");
            for (var b = 4; b >= 0; b--)
                bits[i * 5 + (4 - b)] = (val & (1 << b)) != 0;
        }
        for (var i = 0; i < 128; i++)
            if (bits[i])
                bytes[i / 8] |= (byte)(1 << (7 - (i % 8)));

        // NUlid stored bytes big-endian in the Ulid struct, then used Guid(byte[])
        // which interprets the first 4 bytes as little-endian int32, next 2 as LE int16, etc.
        return new Guid(bytes);
    }

    public ECDsa GetWorkerPrivateKey()
    {
        var parts = GetKeyParts();
        var privateScalar = Convert.FromBase64String(parts.workerPriv);
        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateScalar
        });
    }

    public ECDsa GetServerPublicKey()
    {
        var parts = GetKeyParts();
        var publicKeyBytes = Convert.FromBase64String(parts.serverPub);
        var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
        return ecdsa;
    }

    private (string name, string id, string workerPriv, string serverPub) GetKeyParts()
    {
        var parts = WorkerKey.Split(':');
        if (parts.Length != 4)
            throw new InvalidOperationException(
                "ILMARINEN_WORKER_KEY is malformed. Expected format: {name}:{id}:{workerPrivBase64}:{serverPubBase64}");

        return (parts[0], parts[1], parts[2], parts[3]);
    }
}
