using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace SportShop.Api.Tests;

public sealed class SecurityPipelineTests(SecurityApiFactory factory) : IClassFixture<SecurityApiFactory>, IDisposable
{
    private readonly HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    });

    private string Token(int id, string? version = "0", string role = "Admin", string issuer = "test-issuer",
        string audience = "test-audience", int expiresInMinutes = 10, string? key = null, bool claimedFlag = false)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new("sub", id.ToString()), new("role", role), new("email", "user@example.test"),
            new("jti", Guid.NewGuid().ToString()), new("MustChangePassword", claimedFlag.ToString())
        };
        if (version is not null) claims.Add(new("tv", version));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims), Issuer = issuer, Audience = audience,
            IssuedAt = now.AddMinutes(-20), NotBefore = now.AddMinutes(-20), Expires = now.AddMinutes(expiresInMinutes),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key ?? factory.Key)),
                SecurityAlgorithms.HmacSha256)
        });
    }

    private async Task Check(string method, string path, string? token, int status, string? code = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/v1/" + path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        if (code is null) return;
        if (status == 401) Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement;
        Assert.False(error.GetProperty("success").GetBoolean());
        Assert.Equal(code, error.GetProperty("errorCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("private diagnostic marker", body);
        Assert.DoesNotContain("Exception", body);
    }

    [Fact]
    public async Task ReadsCurrentDatabaseStateOnEveryProtectedRequest()
    {
        var id = await factory.CreateUserAsync();
        var token = Token(id, claimedFlag: true);
        await Check("GET", "admin/orders", token, 200);
        await factory.ChangeStateAsync(id, 0, true);
        await Check("GET", "admin/orders", token, 403, "PASSWORD_CHANGE_REQUIRED");
        await factory.ChangeStateAsync(id, 1, true);
        await Check("GET", "admin/orders", token, 401, "TOKEN_REVOKED");
        await Check("POST", "auth/logout", token, 401, "TOKEN_REVOKED");
        await factory.ChangeStateAsync(id, 1, false);
        await Check("GET", "admin/orders", Token(id, "1"), 200);
    }

    [Theory]
    [InlineData("GET", "users/me", true)]
    [InlineData("PUT", "users/me/password", true)]
    [InlineData("POST", "auth/logout", true)]
    [InlineData("POST", "auth/refresh", true)]
    [InlineData("PUT", "users/me", false)]
    [InlineData("GET", "users/me/addresses", false)]
    [InlineData("GET", "cart", false)]
    public async Task ForcedPasswordChangeUsesMethodAndPathBeforeRole(string method, string path, bool allowed)
    {
        var id = await factory.CreateUserAsync(true);
        await Check(method, path, Token(id), allowed ? 200 : 403, allowed ? null : "PASSWORD_CHANGE_REQUIRED");
    }

    [Fact]
    public async Task CookieRefreshRouteNeedsNoAccessToken()
        => await Check("POST", "auth/refresh", null, 200);

    [Fact]
    public async Task InvalidOrMissingCredentialsAreRejected()
    {
        var id = await factory.CreateUserAsync();
        string?[] invalid = [null, "invalid", Token(id, issuer: "other"), Token(id, audience: "other"),
            Token(id, key: Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48))),
            Token(id, version: null), Token(id, version: "invalid"), Token(int.MaxValue), Token(0), Token(id, role: "Unknown")];
        foreach (var token in invalid) await Check("GET", "users/me", token, 401, "UNAUTHORIZED");
        await Check("GET", "users/me", Token(id, expiresInMinutes: -1), 401, "TOKEN_EXPIRED");
    }

    [Theory]
    [InlineData("Admin", "admin/orders", true)]
    [InlineData("Customer", "admin/orders", false)]
    [InlineData("Customer", "cart", true)]
    [InlineData("Admin", "cart", false)]
    public async Task EnforcesRolePolicies(string role, string route, bool allowed)
    {
        var id = await factory.CreateUserAsync();
        await Check("GET", route, Token(id, role: role), allowed ? 200 : 403, allowed ? null : "FORBIDDEN");
    }

    [Fact]
    public async Task UnexpectedExceptionReturnsSafeJson()
        => await Check("GET", "products", null, 500, "INTERNAL_ERROR");

    [Theory]
    [InlineData("Jwt:SecretKey", "")]
    [InlineData("Jwt:SecretKey", "short")]
    [InlineData("Jwt:Audience", "")]
    [InlineData("Jwt:Issuer", "")]
    public void RefusesToStartWithInvalidJwtConfiguration(string setting, string value)
    {
        using var invalid = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { [setting] = value })));
        Assert.Throws<OptionsValidationException>(() => invalid.CreateClient());
    }

    [Theory]
    [InlineData("https://localhost:5500", true)]
    [InlineData("https://elsewhere.example", false)]
    public async Task CorsPreflightUsesExplicitOriginsBeforeAuthentication(string origin, bool allowed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/users/me");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        using var response = await client.SendAsync(request);
        Assert.Equal(204, (int)response.StatusCode);
        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
        if (allowed)
        {
            Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
        }
    }

    public void Dispose() => client.Dispose();
}
