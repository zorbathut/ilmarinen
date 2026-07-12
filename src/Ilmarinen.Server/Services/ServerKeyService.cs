using System.Security.Cryptography;
using System.Text;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>
/// Manages the server's ECDSA P-256 keypair for worker authentication and derives
/// an AES-256 encryption key for credential storage. Both are sourced from the single
/// ILMARINEN_SERVER_KEY environment variable (base64-encoded 32-byte P-256 private scalar).
/// </summary>
public class ServerKeyService
{
    private const string PlaceholderKey = "REPLACE-ME-WITH-REAL-KEY-GENERATED-VIA-openssl-rand-base64-32";

    private readonly ECDsa? _key;
    private readonly bool _isPlaceholder;
    private readonly byte[]? _publicKeyBytes;
    private readonly byte[]? _encryptionKey;

    public ServerKeyService()
    {
        var keyBase64 = Environment.GetEnvironmentVariable("ILMARINEN_SERVER_KEY");

        if (string.IsNullOrEmpty(keyBase64))
        {
            _key = null;
            _isPlaceholder = false;
            return;
        }

        if (keyBase64 == PlaceholderKey)
        {
            _key = null;
            _isPlaceholder = true;
            return;
        }

        var keyBytes = ParseKeyBytes(keyBase64);

        _key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = keyBytes
        });
        _publicKeyBytes = _key.ExportSubjectPublicKeyInfo();

        // Derive a separate AES-256 key for credential encryption via HKDF
        _encryptionKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            keyBytes,
            outputLength: 32,
            info: Encoding.UTF8.GetBytes("ilmarinen-credential-encryption"));

        _isPlaceholder = false;
    }

    /// <summary>
    /// Decodes and validates a configured key. A malformed key is an operator mistake rather than a bug, so it raises ConfigurationException (503) — not the InvalidOperationException that would surface as a 500.
    /// </summary>
    public static byte[] ParseKeyBytes(string keyBase64)
    {
        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(keyBase64);
        }
        catch (FormatException)
        {
            throw new ConfigurationException(
                "ILMARINEN_SERVER_KEY must be valid base64. Generate one with: openssl rand -base64 32");
        }

        if (keyBytes.Length != 32)
        {
            throw new ConfigurationException(
                "ILMARINEN_SERVER_KEY must be a 32-byte key encoded as base64. Generate one with: openssl rand -base64 32");
        }

        return keyBytes;
    }

    public bool IsEnabled => _key != null;

    public string GetPublicKeyBase64()
    {
        EnsureEnabled();
        return Convert.ToBase64String(_publicKeyBytes!);
    }

    public byte[] Sign(byte[] data)
    {
        EnsureEnabled();
        return _key!.SignData(data, HashAlgorithmName.SHA256);
    }

    public byte[] GetEncryptionKey()
    {
        EnsureEnabled();
        return _encryptionKey!;
    }

    public static byte[] BuildChallengeData(string workerId, byte[] workerNonce, byte[] serverNonce)
    {
        return Encoding.UTF8.GetBytes(
            $"{workerId}:{Convert.ToBase64String(workerNonce)}:{Convert.ToBase64String(serverNonce)}");
    }

    private void EnsureEnabled()
    {
        if (_isPlaceholder)
        {
            throw new ConfigurationException(
                "ILMARINEN_SERVER_KEY is set to the placeholder value. " +
                "Generate a real key with: openssl rand -base64 32");
        }

        if (_key == null)
        {
            throw new ConfigurationException(
                "ILMARINEN_SERVER_KEY is not configured. " +
                "Generate a key with: openssl rand -base64 32");
        }
    }
}
