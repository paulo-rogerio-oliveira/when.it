using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using DbSense.Core.Security;
using DbSense.Core.Tests.Helpers;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;

namespace DbSense.Core.Tests.Security;

public class JwtServiceTests
{
    private static SecurityOptions DefaultOptions() => new()
    {
        EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        JwtSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
        JwtExpirationHours = 8,
        JwtIssuer = "dbsense-test",
        JwtAudience = "dbsense-test"
    };

    [Fact]
    public void IssueToken_Produces_Token_With_Expected_Claims()
    {
        var opts = DefaultOptions();
        var jwt = new JwtService(TestOptions.Wrap(opts));
        var userId = Guid.NewGuid();

        var token = jwt.IssueToken(userId, "alice", "admin");

        var handler = new JwtSecurityTokenHandler();
        var parsed = handler.ReadJwtToken(token);

        parsed.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Sub && c.Value == userId.ToString());
        parsed.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.UniqueName && c.Value == "alice");
        parsed.Claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == "admin");
        parsed.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Jti);
        parsed.Issuer.Should().Be("dbsense-test");
        parsed.Audiences.Should().Contain("dbsense-test");
    }

    [Fact]
    public void IssueToken_Sets_Expiration_From_Options()
    {
        var opts = DefaultOptions();
        opts.JwtExpirationHours = 2;
        var jwt = new JwtService(TestOptions.Wrap(opts));

        var token = jwt.IssueToken(Guid.NewGuid(), "u", "admin");
        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);

        var expected = DateTime.UtcNow.AddHours(2);
        parsed.ValidTo.Should().BeCloseTo(expected, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Issued_Token_Validates_With_GetValidationParameters()
    {
        var opts = DefaultOptions();
        var jwt = new JwtService(TestOptions.Wrap(opts));
        var token = jwt.IssueToken(Guid.NewGuid(), "u", "admin");

        var handler = new JwtSecurityTokenHandler();
        var act = () => handler.ValidateToken(token, jwt.GetValidationParameters(), out _);

        act.Should().NotThrow();
    }

    [Fact]
    public void Token_Issued_With_One_Secret_Fails_Validation_With_Another()
    {
        var jwtA = new JwtService(TestOptions.Wrap(DefaultOptions()));
        var jwtB = new JwtService(TestOptions.Wrap(DefaultOptions()));

        var token = jwtA.IssueToken(Guid.NewGuid(), "u", "admin");

        var act = () => new JwtSecurityTokenHandler().ValidateToken(token, jwtB.GetValidationParameters(), out _);
        act.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void Constructor_Throws_When_Secret_Missing()
    {
        var opts = DefaultOptions();
        opts.JwtSecret = "";
        var act = () => new JwtService(TestOptions.Wrap(opts));
        act.Should().Throw<InvalidOperationException>().WithMessage("*JwtSecret*");
    }

    [Fact]
    public void Constructor_Throws_When_Secret_Too_Short()
    {
        var opts = DefaultOptions();
        opts.JwtSecret = Convert.ToBase64String(new byte[16]); // < 32 bytes
        var act = () => new JwtService(TestOptions.Wrap(opts));
        act.Should().Throw<InvalidOperationException>().WithMessage("*256 bits*");
    }
}
