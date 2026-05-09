using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace DbSense.Core.Security;

public interface ISecretCipher
{
    byte[] Encrypt(string plaintext);
    string Decrypt(byte[] ciphertext);
}

public class SecretCipher : ISecretCipher
{
    private readonly byte[] _key;

    public SecretCipher(IOptions<SecurityOptions> options)
    {
        var raw = options.Value.EncryptionKey;
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                "Security:EncryptionKey is not configured. " +
                "Set the environment variable Security__EncryptionKey (base64-encoded 32 bytes), " +
                "or define Security:EncryptionKey in appsettings.json. " +
                "When launched via the Electron shell, the key is generated automatically into " +
                "%APPDATA%/DbSense/dbsense.config.json on first run.");

        try
        {
            _key = Convert.FromBase64String(raw);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "Security:EncryptionKey is not valid base64. " +
                "Generate a key with: [Convert]::ToBase64String((1..32 | % { [byte](Get-Random -Min 0 -Max 256) })).",
                ex);
        }

        if (_key.Length != 32)
            throw new InvalidOperationException(
                $"Security:EncryptionKey decoded to {_key.Length} bytes; AES-256 requires exactly 32 bytes.");
    }

    public byte[] Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);

        var output = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, output, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, output, nonce.Length + tag.Length, cipher.Length);
        return output;
    }

    public string Decrypt(byte[] ciphertext)
    {
        if (ciphertext.Length < 12 + 16)
            throw new CryptographicException("Ciphertext too short.");

        var nonce = new byte[12];
        var tag = new byte[16];
        var cipher = new byte[ciphertext.Length - 28];
        Buffer.BlockCopy(ciphertext, 0, nonce, 0, 12);
        Buffer.BlockCopy(ciphertext, 12, tag, 0, 16);
        Buffer.BlockCopy(ciphertext, 28, cipher, 0, cipher.Length);

        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Decrypt(nonce, cipher, tag, plain);
        return System.Text.Encoding.UTF8.GetString(plain);
    }
}
