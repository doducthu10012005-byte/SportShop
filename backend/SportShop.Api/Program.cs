using Microsoft.EntityFrameworkCore;
using SportShop.Api.Middleware;
using SportShop.Api.Configuration;
using SportShop.Api.Data;
using SportShop.Api.Auth;
using SportShop.Api.Admin;
using SportShop.Api.PublicCatalog;
using SportShop.Api.ShoppingCart;
using System.Threading.RateLimiting;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// 1. Cấu hình Database Context (SQL Server)
builder.Services.AddDbContext<SportShopDbContext>(options =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
});

builder.Services.AddAccessTokenSecurity(builder.Configuration);
builder.Services.AddSessionManagement();
builder.Services.AddScoped<LookupData>();
builder.Services.AddScoped<ProductData>();
builder.Services.AddScoped<ProductPhotos>();
builder.Services.AddScoped<StockData>();
builder.Services.AddScoped<PublicProducts>();
builder.Services.AddScoped<CatalogLookups>();
builder.Services.AddScoped<CustomerCart>();
builder.Services.AddRateLimiter(options => options.AddPolicy("image-upload", context =>
    RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirst("sub")?.Value ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) })));
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
app.Environment.WebRootPath ??= Path.Combine(app.Environment.ContentRootPath, "wwwroot");
var uploadRoot = Directory.CreateDirectory(Path.Combine(app.Environment.WebRootPath, "uploads", "products"));
var uploadFiles = new PhysicalFileProvider(uploadRoot.FullName);
app.Lifetime.ApplicationStopped.Register(uploadFiles.Dispose);
app.Map("/uploads/products", files => files.UseStaticFiles(new StaticFileOptions
{
    FileProvider = uploadFiles,
    ContentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { [".jpg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp" }),
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.XContentTypeOptions = "nosniff"
}));
app.UseStaticFiles();

app.UseRouting();

// 8. Kích hoạt CORS Policy đã cấu hình tường minh
app.UseCors("CorsPolicy");

// 9. Pipeline Xác thực & Phân quyền
app.UseAuthentication();
app.UseMiddleware<AccountSecurityMiddleware>();
app.UseMiddleware<CookieRequestMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers().RequireAuthorization();

app.Run();

public partial class Program { }
