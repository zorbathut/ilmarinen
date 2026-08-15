using System.Security.Cryptography;
using System;

namespace Ilmarinen.WorkerLauncher;

/// <summary>
/// Extracts the server public key from the combined worker key ({name}:{id}:{workerPrivBase64}:{serverPubBase64}). Deliberately duplicated from the worker's WorkerConfig: the launcher references no Ilmarinen projects because it is the one component that can never auto-update.
/// </summary>
public static class WorkerKey
{
    public static ECDsa GetServerPublicKey(string workerKey)
    {
        var parts = workerKey.Split(':');
        if (parts.Length != 4)
        {
            throw new InvalidOperationException(
                "ILMARINEN_WORKER_KEY is malformed. Expected format: {name}:{id}:{workerPrivBase64}:{serverPubBase64}");
        }

        var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(parts[3]), out _);
        return ecdsa;
    }
}
