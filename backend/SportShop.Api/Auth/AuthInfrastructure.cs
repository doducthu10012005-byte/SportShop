using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using SportShop.Api.Data.Entities;
using SportShop.Api.Middleware;

namespace SportShop.Api.Auth;

internal static class AuthInfrastructure
{
    public static void AddSessionManagement(this IServiceCollection services)
    {
        services.AddScoped<AccountSessions>();
        services.AddScoped<SessionTokens>();
        services.AddSingleton(_ => PartitionedRateLimiter.Create<string, string>(email =>
            RateLimitPartition.GetFixedWindowLimiter(email, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20, Window = TimeSpan.FromMinutes(1)
            })));
        services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
        services.PostConfigure<ApiBehaviorOptions>(o => o.InvalidModelStateResponseFactory = action =>
        {
            var errors = action.ModelState.Where(e => e.Value?.Errors.Count > 0)
                .Select(e => new { field = JsonNamingPolicy.CamelCase.ConvertName(e.Key), message = "Giá trị không hợp lệ." });
            return new BadRequestObjectResult(new { success = false, message = "Dữ liệu không hợp lệ.",
                errorCode = "VALIDATION_ERROR", errors, traceId = ApiError.TraceId(action.HttpContext) });
        });
        services.AddRateLimiter(o =>
        {
            o.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
                (context.Connection.RemoteIpAddress?.ToString(), context.GetEndpoint()?.DisplayName),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
            o.OnRejected = (rejected, _) => new ValueTask(ApiError.WriteAsync(rejected.HttpContext, 429,
                "TOO_MANY_REQUESTS", "Quá nhiều yêu cầu. Vui lòng thử lại sau."));
        });
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class CookieSessionAttribute : Attribute { }

internal sealed class CookieRequestMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, Microsoft.Extensions.Options.IOptions<Configuration.CorsOptions> cors)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<CookieSessionAttribute>() is null)
        {
            await next(context);
            return;
        }

        var request = context.Request;
        bool Allowed(string header, bool isOrigin)
        {
            if (!request.Headers.TryGetValue(header, out var values)) return true;
            if (values.Count != 1 || !Uri.TryCreate(values[0], UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0) return false;
            var origin = uri.GetLeftPart(UriPartial.Authority);
            if (isOrigin && !string.Equals(origin, values[0], StringComparison.OrdinalIgnoreCase)) return false;
            return string.Equals(origin, request.Scheme + "://" + request.Host, StringComparison.OrdinalIgnoreCase) ||
                cors.Value.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
        }

        if (request.Headers["X-Requested-With"].ToString() != "XMLHttpRequest" ||
            !Allowed("Origin", true) || !Allowed("Referer", false))
            await ApiError.WriteAsync(context, 403, "FORBIDDEN", "Yêu cầu không được phép.");
        else if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
                 !string.Equals(contentType.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase))
            await ApiError.WriteAsync(context, 415, "UNSUPPORTED_MEDIA_TYPE", "Yêu cầu phải dùng application/json.");
        else
            await next(context);
    }
}
