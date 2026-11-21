using Microsoft.EntityFrameworkCore;
using SportShop.Api.Middleware;
using SportShop.Api.Configuration;
using SportShop.Api.Data;
using SportShop.Api.Auth;

var builder = WebApplication.CreateBuilder(args);

// 1. Cấu hình Database Context (SQL Server)
builder.Services.AddDbContext<SportShopDbContext>(options =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
});

builder.Services.AddAccessTokenSecurity(builder.Configuration);
builder.Services.AddSessionManagement();
builder.Services.Configure<CorsOptions>(builder.Configuration.GetSection(CorsOptions.SectionName));

// 3. Cấu hình CORS (Tuân thủ System Overview: Danh sách tường minh, hỗ trợ credentials, không dùng AllowAnyOrigin)
builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy", policy =>
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        if (allowedOrigins.Length > 0)
        {
            if (allowedOrigins.Any(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 ||
                origin.Contains('*') || !string.Equals(origin, uri.GetLeftPart(UriPartial.Authority),
                    StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Cors:AllowedOrigins requires explicit HTTPS origins.");

            policy.WithOrigins(allowedOrigins)
                  .AllowCredentials()
                  .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
                  .WithHeaders("Authorization", "Content-Type", "X-Requested-With")
                  .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
        }
    });
});

// 5. Controller & OpenAPI
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();
if (args.FirstOrDefault() == "reset-admin-password")
{
    Environment.ExitCode = args.Length == 1 ? await AdminMaintenance.ResetInteractiveAsync(app.Services) : 1;
    await app.DisposeAsync();
    return;
}
await AdminMaintenance.BootstrapAsync(app.Services, app.Configuration);
app.UseMiddleware<ExceptionMiddleware>();

// 6. Cấu hình HTTP Request Pipeline
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    // Production: Bắt buộc HSTS theo system-overview.md
    app.UseHsts();
}

app.UseHttpsRedirection();

// 7. Phục vụ Static Files (Hỗ trợ Phương án A: Cùng Origin phục vụ Frontend & Ảnh wwwroot/uploads)
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();

// 8. Kích hoạt CORS Policy đã cấu hình tường minh
app.UseCors("CorsPolicy");

// 9. Pipeline Xác thực & Phân quyền
app.UseAuthentication();
app.UseMiddleware<AccountSecurityMiddleware>();
app.UseMiddleware<CookieRequestMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers().RequireAuthorization();

app.Run();

public partial class Program { }
