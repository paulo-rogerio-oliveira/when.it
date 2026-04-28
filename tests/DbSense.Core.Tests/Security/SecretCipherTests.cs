using System.Security.Cryptography;
using DbSense.Core.Security;
using DbSense.Core.Tests.Helpers;
using FluentAssertions;

namespace DbSense.Core.Tests.Security;

public class SecretCipherTests
{
    private static SecretCipher Create()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return new SecretCipher(TestOptions.Wrap(new SecurityOptions { EncryptionKey = key }));
    }

    [Fact]
    public void Encrypt_Decrypt_Roundtrips_Plaintext()
    {
        var cipher = Create();
        var plaintext = "Sup3rS3cret!@# senha do banco";

        var encrypted = cipher.Encrypt(plaintext);
        var decrypted = cipher.Decrypt(encrypted);

        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_Produces_Different_Output_Each_Call_Due_To_Random_Nonce()
    {
        var cipher = Create();
        var a = cipher.Encrypt("payload");
        var b = cipher.Encrypt("payload");

        a.Should().NotEqual(b);
    }

    [Fact]
    public void Constructor_Throws_When_Key_Is_Missing()
    {
        var act = () => new SecretCipher(TestOptions.Wrap(new SecurityOptions { EncryptionKey = "" }));
        act.Should().Throw<InvalidOperationException>().WithMessage("*EncryptionKey*");
    }

    [Fact]
    public void Constructor_Throws_When_Key_Is_Wrong_Size()
    {
        var shortKey = Convert.ToBase64String(new byte[16]);
        var act = () => new SecretCipher(TestOptions.Wrap(new SecurityOptions { EncryptionKey = shortKey }));
        act.Should().Throw<InvalidOperationException>().WithMessage("*32 bytes*");
    }

    [Fact]
    public void Decrypt_Fails_On_Tampered_Ciphertext()
    {
        var cipher = Create();
        var encrypted = cipher.Encrypt("hello");
        encrypted[encrypted.Length - 1] ^= 0xFF; // flip last byte (cipher)

        var act = () => cipher.Decrypt(encrypted);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Decrypt_Throws_On_Truncated_Ciphertext()
    {
        var cipher = Create();
        var act = () => cipher.Decrypt(new byte[5]);
        act.Should().Throw<CryptographicException>().WithMessage("*too short*");
    }

    [Fact]
    public void Decrypt_Fails_With_Different_Key()
    {
        var cipherA = Create();
        var cipherB = Create();
        var encrypted = cipherA.Encrypt("payload");

        var act = () => cipherB.Decrypt(encrypted);
        act.Should().Throw<CryptographicException>();
    }
}
