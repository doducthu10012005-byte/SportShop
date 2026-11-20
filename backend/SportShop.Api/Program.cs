using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SportShop.Api.Configuration;

var builder = WebApplication.CreateBuilder(args);

// 1. Cấu hình Options Pattern
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<CorsOptions>(builder.Configuration.GetSection(CorsOptions.SectionName));

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

// 2. Cấu hình CORS (Tuân thủ System Overview: Danh sách tường minh, hỗ trợ credentials, không dùng AllowAnyOrigin)
builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy", policy =>
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowCredentials()
                  .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
                  .WithHeaders("Authorization", "Content-Type", "X-Requested-With")
                  .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
        }
    });
});

// 3. Cấu hình Xác thực JWT Bearer
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    options.SaveToken = true;

    var signingKeyBytes = !string.IsNullOrWhiteSpace(jwtOptions.SecretKey)
        ? Encoding.UTF8.GetBytes(jwtOptions.SecretKey)
        : new byte[32];

    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(signingKeyBytes),
        ValidateIssuer = !string.IsNullOrWhiteSpace(jwtOptions.Issuer),
        ValidIssuer = jwtOptions.Issuer,
        ValidateAudience = !string.IsNullOrWhiteSpace(jwtOptions.Audience),
        ValidAudience = jwtOptions.Audience,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero
    };
});

builder.Services.AddAuthorization();

// 4. Controller & OpenAPI
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

// 5. Cấu hình HTTP Request Pipeline
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

// 6. Phục vụ Static Files (Hỗ trợ Phương án A: Cùng Origin phục vụ Frontend & Ảnh wwwroot/uploads)
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();

// 7. Kích hoạt CORS Policy đã cấu hình tường minh
app.UseCors("CorsPolicy");

// 8. Pipeline Xác thực & Phân quyền
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
