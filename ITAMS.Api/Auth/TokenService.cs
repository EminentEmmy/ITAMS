using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ITAMS.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ITAMS.Api.Auth;

public interface ITokenService
{
    string CreateAccessToken(AppUser user);
    (string Raw, string Hash) CreateRefreshToken();
    string Hash(string rawToken);
}

public sealed class TokenService : ITokenService
{
    private readonly JwtOptions _options;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SigningCredentials _credentials;

    public TokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
        _credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key)),
            SecurityAlgorithms.HmacSha256);
    }

    public string CreateAccessToken(AppUser user)
    {
        // Short claim names on purpose: sub = user, tid = tenant, role = RBAC, mcp = must change password.
        var claims = new List<Claim>
        {
            new("sub", user.Id.ToString()),
            new("tid", user.TenantId),
            new("role", user.Role.ToString()),
            new("name", user.FullName),
            new("email", user.Email)
        };
        if (user.MustChangePassword) claims.Add(new Claim("mcp", "1"));

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Expires = DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes),
            SigningCredentials = _credentials
        };

        return _handler.CreateToken(descriptor);
    }

    public (string Raw, string Hash) CreateRefreshToken()
    {
        var raw = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(64));
        return (raw, Hash(raw));
    }

    public string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
