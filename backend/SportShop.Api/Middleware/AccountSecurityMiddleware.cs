using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;

namespace SportShop.Api.Middleware;

internal sealed class AccountSecurityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SportShopDbContext db)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAuthorizeData>() is null ||
            endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null ||
            context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        if (!ReadClaim(context, "sub", out var userId) || userId <= 0 ||
            !ReadClaim(context, "tv", out var tokenVersion))
        {
            await Unauthorized(context);
            return;
        }

        var account = await db.Users.AsNoTracking()
            .Where(u => u.UserId == userId)
            .Select(u => new { u.TokenVersion, u.MustChangePassword })
            .SingleOrDefaultAsync(context.RequestAborted);

        if (account is null)
            await Unauthorized(context);
        else if (tokenVersion != account.TokenVersion)
            await ApiError.WriteAsync(context, 401, "TOKEN_REVOKED", "Phiên đăng nhập đã bị thu hồi.");
        else if (account.MustChangePassword && !AllowedDuringPasswordChange(context.Request))
            await ApiError.WriteAsync(context, 403, "PASSWORD_CHANGE_REQUIRED", "Bạn cần đổi mật khẩu để tiếp tục.");
        else
            await next(context);
    }

    private static bool ReadClaim(HttpContext context, string name, out int value) =>
        int.TryParse(context.User.FindFirst(name)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static Task Unauthorized(HttpContext context) =>
        ApiError.WriteAsync(context, 401, "UNAUTHORIZED", "Thông tin xác thực không hợp lệ.");

    private static bool AllowedDuringPasswordChange(HttpRequest request)
    {
        var path = request.Path;
        return (HttpMethods.IsGet(request.Method) && path == "/api/v1/users/me") ||
               (HttpMethods.IsPut(request.Method) && path == "/api/v1/users/me/password") ||
               (HttpMethods.IsPost(request.Method) &&
                (path == "/api/v1/auth/logout" || path == "/api/v1/auth/refresh"));
    }
}
