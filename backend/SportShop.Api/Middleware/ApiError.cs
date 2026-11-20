using System.Diagnostics;

namespace SportShop.Api.Middleware;

internal static class ApiError
{
    public static string TraceId(HttpContext context) => Activity.Current?.Id ?? context.TraceIdentifier;

    public static Task WriteAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        if (status == StatusCodes.Status401Unauthorized)
            context.Response.Headers.WWWAuthenticate = "Bearer";

        return context.Response.WriteAsJsonAsync(new
        {
            success = false,
            message,
            errorCode = code,
            traceId = TraceId(context)
        }, context.RequestAborted);
    }
}
