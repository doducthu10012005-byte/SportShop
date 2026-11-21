using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;

namespace SportShop.Api.Tests;

public sealed class SecurityApiFactory : WebApplicationFactory<Program>
{
    internal readonly string Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    private readonly string databaseName = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = Key, ["Jwt:Issuer"] = "test-issuer", ["Jwt:Audience"] = "test-audience",
                ["Cors:AllowedOrigins:0"] = "https://localhost:5500"
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDbContextOptionsConfiguration<SportShopDbContext>>();
            services.AddDbContext<SportShopDbContext>(o => o.UseInMemoryDatabase(databaseName));
            services.AddControllers().ConfigureApplicationPartManager(parts =>
            {
                // Phase 5 tests run probe actions to isolate middleware outcomes.
                parts.ApplicationParts.Clear();
                parts.ApplicationParts.Add(new Microsoft.AspNetCore.Mvc.ApplicationParts.AssemblyPart(
                    typeof(SecurityProbeController).Assembly));
            });
        });
    }

    internal async Task<int> CreateUserAsync(bool mustChange = false)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportShopDbContext>();
        var user = new User { MustChangePassword = mustChange };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.UserId;
    }

    internal async Task ChangeStateAsync(int id, int version, bool mustChange)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportShopDbContext>();
        var user = await db.Users.SingleAsync(u => u.UserId == id);
        user.TokenVersion = version;
        user.MustChangePassword = mustChange;
        await db.SaveChangesAsync();
    }
}

// Routes exist only in the test host. They exercise the actual application pipeline.
[ApiController]
public sealed class SecurityProbeController : ControllerBase
{
    [HttpGet("/api/v1/users/me"), HttpPut("/api/v1/users/me/password"), HttpPost("/api/v1/auth/logout")]
    [HttpPut("/api/v1/users/me"), HttpGet("/api/v1/users/me/addresses")]
    public IActionResult Account() => Ok();

    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    [HttpPost("/api/v1/auth/refresh")]
    public IActionResult CookieRoute() => Ok();

    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "Admin"), HttpGet("/api/v1/admin/orders")]
    public IActionResult Admin() => Ok();

    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "Customer"), HttpGet("/api/v1/cart")]
    public IActionResult Cart() => Ok();

    [Microsoft.AspNetCore.Authorization.AllowAnonymous, HttpGet("/api/v1/products")]
    public IActionResult ExceptionProbe() => throw new InvalidOperationException("private diagnostic marker");
}
