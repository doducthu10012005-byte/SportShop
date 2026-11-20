namespace SportShop.Api.Middleware;

internal sealed class ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
        }
        catch (Exception ex)
        {
            // Exception messages may contain credentials or request data.
            logger.LogError("Request {TraceId} failed: {ExceptionType}\n{StackTrace}",
                ApiError.TraceId(context), ex.GetType().FullName, ex.StackTrace);
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }

            context.Response.Clear();
            await ApiError.WriteAsync(context, 500, "INTERNAL_ERROR", "Đã xảy ra lỗi hệ thống.");
        }
    }
}
