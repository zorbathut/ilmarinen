using System.Security.Cryptography;
using System.Text;

namespace Ilmarinen.Server.Services;

/// <summary>
/// Encrypts and decrypts credentials (e.g., Git tokens) using AES-256-GCM.
/// Key is read from ILMARINEN_CREDENTIAL_KEY environment variable.
/// </summary>
public class CredentialEncryptionService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string PlaceholderKey = "REPLACE-ME-WITH-REAL-KEY-GENERATED-VIA-openssl-rand-base64-32";

    private readonly byte[]? _key;
    private readonly bool _isPlaceholder;

    public CredentialEncryptionService()
    {
        var keyBase64 = Environment.GetEnvironmentVariable("ILMARINEN_CREDENTIAL_KEY");

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

        try
        {
            _key = Convert.FromBase64String(keyBase64);
            if (_key.Length != 32)
            {
                throw new InvalidOperationException(
                    "ILMARINEN_CREDENTIAL_KEY must be a 32-byte (256-bit) key encoded as base64.");
            }
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                "ILMARINEN_CREDENTIAL_KEY must be valid base64. Generate one with: openssl rand -base64 32 " +
                "and configure it in docker-compose.override.yml (see docker-compose.override.yml.example).");
        }

        _isPlaceholder = false;
    }

    /// <summary>
    /// True if a valid encryption key is configured.
    /// </summary>
    public bool IsEnabled => _key != null;

    /// <summary>
    /// Encrypts a plaintext token. Returns base64-encoded ciphertext (nonce + ciphertext + tag).
    /// Throws if encryption is not properly configured (placeholder or missing key).
    /// </summary>
    public string? Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return null;

        if (_isPlaceholder)
        {
            throw new InvalidOperationException(
                "Cannot store Git tokens: ILMARINEN_CREDENTIAL_KEY is set to the placeholder value. " +
                "Generate a real key with: openssl rand -base64 32 " +
                "and configure it in docker-compose.override.yml (see docker-compose.override.yml.example).");
        }

        if (_key == null)
        {
            throw new InvalidOperationException(
                "Cannot store Git tokens: ILMARINEN_CREDENTIAL_KEY is not configured. " +
                "Generate a key with: openssl rand -base64 32 " +
                "and configure it in docker-compose.override.yml (see docker-compose.override.yml.example).");
        }

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Combine: nonce + ciphertext + tag
        var combined = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, combined, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length + ciphertext.Length, tag.Length);

        return Convert.ToBase64String(combined);
    }

    /// <summary>
    /// Decrypts a base64-encoded ciphertext back to plaintext.
    /// </summary>
    public string? Decrypt(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted))
            return null;

        if (_isPlaceholder)
        {
            throw new InvalidOperationException(
                "Cannot decrypt Git tokens: ILMARINEN_CREDENTIAL_KEY is set to the placeholder value. " +
                "Configure the real key that was used to encrypt the data.");
        }

        if (_key == null)
        {
            throw new InvalidOperationException(
                "Cannot decrypt: ILMARINEN_CREDENTIAL_KEY not configured but encrypted data found.");
        }

        var combined = Convert.FromBase64String(encrypted);
        if (combined.Length < NonceSize + TagSize)
        {
            throw new InvalidOperationException("Invalid encrypted data: too short.");
        }

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertextLength = combined.Length - NonceSize - TagSize;
        var ciphertext = new byte[ciphertextLength];
        var plaintext = new byte[ciphertextLength];

        Buffer.BlockCopy(combined, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(combined, NonceSize, ciphertext, 0, ciphertextLength);
        Buffer.BlockCopy(combined, NonceSize + ciphertextLength, tag, 0, TagSize);

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }
}
