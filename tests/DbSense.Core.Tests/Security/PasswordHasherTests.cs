using DbSense.Core.Security;
using FluentAssertions;

namespace DbSense.Core.Tests.Security;

public class PasswordHasherTests
{
    private readonly BCryptPasswordHasher _hasher = new();

    [Fact]
    public void Hash_Returns_BCrypt_Formatted_String()
    {
        var hash = _hasher.Hash("password123");
        hash.Should().StartWith("$2");
    }

    [Fact]
    public void Hash_Produces_Different_Output_For_Same_Input_Due_To_Salt()
    {
        var a = _hasher.Hash("password123");
        var b = _hasher.Hash("password123");
        a.Should().NotBe(b);
    }

    [Fact]
    public void Verify_Returns_True_For_Correct_Password()
    {
        var hash = _hasher.Hash("password123");
        _hasher.Verify("password123", hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_Returns_False_For_Wrong_Password()
    {
        var hash = _hasher.Hash("password123");
        _hasher.Verify("nope", hash).Should().BeFalse();
    }
}
