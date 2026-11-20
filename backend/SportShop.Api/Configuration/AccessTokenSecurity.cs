using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SportShop.Api.Middleware;

namespace SportShop.Api.Configuration;

internal static class AccessTokenSecurity
{
    public static void AddAccessTokenSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer) && !string.IsNullOrWhiteSpace(o.Audience),
                "Jwt issuer and audience are required.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.SecretKey) && Encoding.UTF8.GetByteCount(o.SecretKey) >= 32,
                "Jwt signing key must be configured with at least 32 bytes.")
            .Validate(o => o.AccessTokenLifetimeMinutes > 0 && o.RefreshTokenLifetimeDays > 0,
                "Token lifetimes must be positive.")
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((options, configured) =>
            {
                var jwt = configured.Value;
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.IncludeErrorDetails = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SecretKey)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                    NameClaimType = "sub",
                    RoleClaimType = "role"
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var roles = context.Principal!.FindAll("role").ToArray();
                        if (roles.Length != 1 || roles[0].Value is not ("Customer" or "Admin"))
                            context.Fail("Invalid access token role.");
                        return Task.CompletedTask;
                    },
                    OnChallenge = context =>
                    {
                        context.HandleResponse();
                        return context.AuthenticateFailure is SecurityTokenExpiredException
                            ? ApiError.WriteAsync(context.HttpContext, 401, "TOKEN_EXPIRED", "Phiên đăng nhập đã hết hạn.")
                            : ApiError.WriteAsync(context.HttpContext, 401, "UNAUTHORIZED", "Vui lòng đăng nhập bằng token hợp lệ.");
                    },
                    OnForbidden = context => ApiError.WriteAsync(context.HttpContext, 403, "FORBIDDEN",
                        "Bạn không có quyền thực hiện thao tác này.")
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy("Customer", p => p.RequireAuthenticatedUser().RequireRole("Customer"))
            .AddPolicy("Admin", p => p.RequireAuthenticatedUser().RequireRole("Admin"));
    }
}
