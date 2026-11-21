using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using SportShop.Api.Auth;
using SportShop.Api.Data;
using Xunit;

namespace SportShop.Api.Tests;

public sealed class SqlDevelopmentFactAttribute : FactAttribute
{
    public SqlDevelopmentFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SPORTSHOP_AUTH_SQL_TESTS") != "1")
            Skip = "Set SPORTSHOP_AUTH_SQL_TESTS=1 to run with Development SQL Server User Secrets.";
    }
}

public sealed class AuthSqlTests
{
    [Theory]
    [InlineData("short", false)]
    [InlineData("lowercase123", false)]
    [InlineData("UPPERCASE123", false)]
    [InlineData("MissingNumber", false)]
    [InlineData("Abcdef12", true)]
    public void PasswordPolicy(string value, bool valid) => Assert.Equal(valid, new StrongPasswordAttribute().IsValid(value));

    [SqlDevelopmentFact]
    public Task AuthLifecycleOnSqlServer() => TemporarySqlDatabase.Run(RunAuthChecks);

    private static string Password() => "Aa1" + Convert.ToHexString(RandomNumberGenerator.GetBytes(18));

    private static async Task RunAuthChecks(string connection, DbContextOptions<SportShopDbContext> options)
    {
        var adminEmail = "admin@example.test";
        var adminPassword = Password();
        var customerEmail = "customer@example.test";
        var password = Password();
        var changedPassword = Password();
        using var root = new WebApplicationFactory<Program>();
        using var factory = root.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connection,
                ["AdminBootstrap:Enabled"] = "true", ["AdminBootstrap:Email"] = adminEmail,
                ["AdminBootstrap:Password"] = adminPassword,
                ["Jwt:SecretKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            }));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false
        });
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? payload = null,
            string? bearer = null, string? cookie = null, string? header = null, string? origin = null, string? referer = null)
        {
            using var req = new HttpRequestMessage(method, "/api/v1/" + path);
            if (payload is not null) req.Content = JsonContent.Create(payload);
            if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (cookie is not null) req.Headers.Add("Cookie", cookie);
            if (header is not null) req.Headers.Add("X-Requested-With", header);
            if (origin is not null) req.Headers.Add("Origin", origin);
            if (referer is not null) req.Headers.Add("Referer", referer);
            return await client.SendAsync(req);
        }

        static async Task Error(HttpResponseMessage response, int status, string code)
        {
            using (response)
            {
                Assert.Equal(status, (int)response.StatusCode);
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(code, body.GetProperty("errorCode").GetString());
            }
        }
        static async Task<(string Access, string Cookie, bool Forced)> Session(HttpResponseMessage response)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
                foreach (var attr in new[] { "httponly", "secure", "samesite=strict", "path=/api/v1/auth", "max-age=604800" })
                    Assert.Contains(attr, cookie.Split(';').Skip(1).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
                var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
                Assert.False(data.TryGetProperty("refreshToken", out _));
                return (data.GetProperty("accessToken").GetString()!, cookie.Split(';')[0],
                    data.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
            }
        }
        Task<HttpResponseMessage> Login(string email, string pwd) => Send(HttpMethod.Post,"auth/login",new { email, password = pwd });
        Task<HttpResponseMessage> Refresh(string cookie) => Send(HttpMethod.Post,"auth/refresh",new {},cookie: cookie,header:"XMLHttpRequest");
        var registration = new { email = customerEmail.ToUpperInvariant(), fullName = "Customer Test", password,
            confirmPassword = password, role = "Admin" };
        using var registered = await Send(HttpMethod.Post,"auth/register",registration);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        await Error(await Send(HttpMethod.Post,"auth/register",registration),409,"EMAIL_ALREADY_EXISTS");
        var customer = await Session(await Login(customerEmail,password));
        var jwt = new JsonWebToken(customer.Access);
        foreach (var claim in new[] {"sub","email","role","jti","iat","exp","tv"}) Assert.Contains(claim, jwt.Claims.Select(c => c.Type));
        Assert.Equal("Customer", jwt.GetClaim("role").Value);
        Assert.False(customer.Forced);
        using var me = await Send(HttpMethod.Get,"users/me",bearer: customer.Access);
        Assert.Equal(customerEmail,(await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("email").GetString());

        await using (var db = new SportShopDbContext(options))
        {
            var row = await db.RefreshTokens.SingleAsync();
            Assert.True(row.TokenHash == SessionTokens.Hash(customer.Cookie.Split('=')[1]));
            Assert.Equal(1, await db.Carts.CountAsync());
            Assert.True((await db.Users.SingleAsync(u => u.Email == customerEmail)).PasswordHash != password);
        }
        foreach (var path in new[] {"auth/refresh","auth/logout"})
        {
            foreach (var badHeader in new string?[] {null, "wrong"})
                await Error(await Send(HttpMethod.Post,path,new {},customer.Access,customer.Cookie,badHeader),403,"FORBIDDEN");
            await Error(await Send(HttpMethod.Post,path,new {},customer.Access,customer.Cookie,"XMLHttpRequest","https://other.test"),403,"FORBIDDEN");
            await Error(await Send(HttpMethod.Post,path,new {},customer.Access,customer.Cookie,"XMLHttpRequest",referer:"https://other.test/path"),403,"FORBIDDEN");
            await Error(await Send(HttpMethod.Post,path,null,customer.Access,customer.Cookie,"XMLHttpRequest"),415,"UNSUPPORTED_MEDIA_TYPE");
        }

        var pair = await Task.WhenAll(Refresh(customer.Cookie), Refresh(customer.Cookie));
        var rotated = await Session(Assert.Single(pair, r => r.IsSuccessStatusCode));
        await Error(Assert.Single(pair, r => !r.IsSuccessStatusCode),401,"UNAUTHORIZED");
        var otherDevice = await Session(await Login(customerEmail,password));
        RequestPassword change(string current, string next) => new(current,next,next);
        using var changeResult = await Send(HttpMethod.Put,"users/me/password",change(password,changedPassword),rotated.Access);
        Assert.Equal(HttpStatusCode.OK,changeResult.StatusCode);
        await Error(await Send(HttpMethod.Get,"users/me",bearer: otherDevice.Access),401,"TOKEN_REVOKED");
        await Error(await Refresh(rotated.Cookie),401,"UNAUTHORIZED");
        await Error(await Refresh(otherDevice.Cookie),401,"UNAUTHORIZED");
        await Error(await Login(customerEmail,password),401,"INVALID_CREDENTIALS");
        var fresh = await Session(await Login(customerEmail,changedPassword));
        Assert.Equal("1",new JsonWebToken(fresh.Access).GetClaim("tv").Value);
        var expired = await Session(await Login(customerEmail,changedPassword));
        await using (var db = new SportShopDbContext(options))
        {
            var hash = SessionTokens.Hash(expired.Cookie.Split('=')[1]);
            var record = await db.RefreshTokens.SingleAsync(t => t.TokenHash == hash);
            record.CreatedAt = DateTime.UtcNow.AddDays(-8);
            record.ExpiresAt = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }
        await Error(await Refresh(expired.Cookie),401,"UNAUTHORIZED");

        var admin = await Session(await Login(adminEmail,adminPassword));
        Assert.True(admin.Forced);
        using var foreignLogout = await Send(HttpMethod.Post,"auth/logout",new {},admin.Access,fresh.Cookie,"XMLHttpRequest");
        Assert.Equal(HttpStatusCode.OK,foreignLogout.StatusCode);
        var own = await Session(await Refresh(fresh.Cookie));
        for (var i = 0; i < 2; i++)
        {
            using var logout = await Send(HttpMethod.Post,"auth/logout",new {},own.Access,own.Cookie,"XMLHttpRequest");
            Assert.Equal(HttpStatusCode.OK,logout.StatusCode);
            Assert.True(logout.Headers.GetValues("Set-Cookie").Single().Contains("max-age=0",StringComparison.OrdinalIgnoreCase));
        }
        await Error(await Refresh(own.Cookie),401,"UNAUTHORIZED");

        var config = factory.Services.GetRequiredService<IConfiguration>();
        await AdminMaintenance.BootstrapAsync(factory.Services, config);
        using var scope = factory.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountSessions>();
        var reset = Password();
        Assert.False(await accounts.ResetAdminAsync(customerEmail,reset,default));
        Assert.True(await accounts.ResetAdminAsync(adminEmail,reset,default));
        await Error(await Send(HttpMethod.Get,"users/me",bearer:admin.Access),401,"TOKEN_REVOKED");
        await Error(await Refresh(admin.Cookie),401,"UNAUTHORIZED");
        var temporary = await Session(await Login(adminEmail,reset));
        Assert.True(temporary.Forced);
        var finalPassword = Password();
        using var finalChange = await Send(HttpMethod.Put,"users/me/password",change(reset,finalPassword),temporary.Access);
        Assert.Equal(HttpStatusCode.OK,finalChange.StatusCode);
        Assert.False((await Session(await Login(adminEmail,finalPassword))).Forced);
        await using var finalDb = new SportShopDbContext(options);
        Assert.Equal(1, await finalDb.Users.CountAsync(u => u.Role.RoleName == "Admin"));
        var beforeLock = await Session(await Login(customerEmail,changedPassword));
        await finalDb.Users.Where(u => u.Email == customerEmail)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        await Error(await Login(customerEmail,changedPassword),403,"ACCOUNT_LOCKED");
        await Error(await Refresh(beforeLock.Cookie),401,"UNAUTHORIZED");
    }

    private sealed record RequestPassword(string CurrentPassword,string NewPassword,string ConfirmNewPassword);
}
