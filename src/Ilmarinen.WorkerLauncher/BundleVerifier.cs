using System.IO;
using System.Security.Cryptography;
using System.Text;
using System;

namespace Ilmarinen.WorkerLauncher;

/// <summary>
/// Verifies a downloaded bundle before it is ever extracted or executed: the zip must hash to the manifest's bundleHash, and the manifest's signature over that hash must verify against the server public key from ILMARINEN_WORKER_KEY. The worker channel is plaintext HTTP, so this is what stops a network MITM from injecting code.
/// </summary>
public static class BundleVerifier
{
    // Must match WorkerBundleService.SignaturePrefix on the server; the prefix domain-separates bundle signatures from auth-challenge signatures.
    public const string SignaturePrefix = "ilmarinen-worker-bundle:";

    public static bool Verify(ECDsa serverPublicKey, Stream zipContent, BundleManifest manifest)
    {
        var actualHash = Convert.ToHexString(SHA256.HashData(zipContent));
        if (!string.Equals(actualHash, manifest.BundleHash, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(manifest.Signature);
        }
        catch (FormatException)
        {
            return false;
        }

        var signedData = Encoding.UTF8.GetBytes(SignaturePrefix + manifest.BundleHash);
        return serverPublicKey.VerifyData(signedData, signature, HashAlgorithmName.SHA256);
    }
}
