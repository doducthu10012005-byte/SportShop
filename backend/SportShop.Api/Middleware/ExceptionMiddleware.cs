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
        catch (ApiFailure failure) when (!context.Response.HasStarted)
        {
            if (failure.Errors.Count == 0)
                await ApiError.WriteAsync(context, failure.Status, failure.Code, failure.Message);
            else
            {
                context.Response.StatusCode = failure.Status;
                await context.Response.WriteAsJsonAsync(new { success = false, message = failure.FieldErrorSummary,
                    errorCode = failure.Code, errors = failure.Errors,
                    traceId = ApiError.TraceId(context) }, context.RequestAborted);
            }
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
