using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SportShop.Api.Configuration;
using SportShop.Api.Data.Entities;

namespace SportShop.Api.Auth;

public sealed class SessionTokens(IOptions<JwtOptions> options)
{
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    internal IssuedSession Issue(User user, DateTime now)
    {
        var jwt = options.Value;
        var expires = now.AddMinutes(jwt.AccessTokenLifetimeMinutes);
        var access = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer, Audience = jwt.Audience, IssuedAt = now, NotBefore = now, Expires = expires,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = user.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["email"] = user.Email, ["role"] = user.Role.RoleName,
                ["jti"] = Guid.NewGuid().ToString("N"), ["tv"] = user.TokenVersion
            },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SecretKey)),
                SecurityAlgorithms.HmacSha256)
        });
        var raw = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        var lifetime = TimeSpan.FromDays(jwt.RefreshTokenLifetimeDays);
        user.RefreshTokens.Add(new RefreshToken { TokenHash = Hash(raw), CreatedAt = now, ExpiresAt = now + lifetime });
        var profile = new SessionUser(user.UserId, user.FullName, user.Email, user.Role.RoleName, user.MustChangePassword);
        return new IssuedSession(new SessionBody(access, expires, profile), raw, lifetime);
    }
}
