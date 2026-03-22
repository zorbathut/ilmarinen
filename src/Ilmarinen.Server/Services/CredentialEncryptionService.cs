using System.Security.Cryptography;
using System.Text;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>
/// Encrypts and decrypts credentials (e.g., Git tokens) using AES-256-GCM.
/// The encryption key is derived from ILMARINEN_SERVER_KEY via HKDF.
/// </summary>
public class CredentialEncryptionService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly ServerKeyService _serverKey;

    public CredentialEncryptionService(ServerKeyService serverKey)
    {
        _serverKey = serverKey;
    }

    /// <summary>
    /// True if a valid encryption key is configured.
    /// </summary>
    public bool IsEnabled => _serverKey.IsEnabled;

    /// <summary>
    /// Encrypts a plaintext token. Returns base64-encoded ciphertext (nonce + ciphertext + tag).
    /// </summary>
    public string? Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return null;

        var key = _serverKey.GetEncryptionKey();

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
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

        var key = _serverKey.GetEncryptionKey();

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

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }
}
