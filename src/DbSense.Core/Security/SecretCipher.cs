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
            throw new InvalidOperationException("Security:EncryptionKey is not configured.");

        _key = Convert.FromBase64String(raw);
        if (_key.Length != 32)
            throw new InvalidOperationException("Security:EncryptionKey must decode to 32 bytes (AES-256).");
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
