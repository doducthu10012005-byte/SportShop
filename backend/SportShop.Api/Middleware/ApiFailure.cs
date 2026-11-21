namespace SportShop.Api.Middleware;

internal class ApiFailure(int status, string code, string message, string? field = null,
    string fieldErrorSummary = "Dữ liệu không hợp lệ.") : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string FieldErrorSummary { get; } = fieldErrorSummary;
    public IReadOnlyList<ApiFieldError> Errors { get; init; } =
        field is null ? [] : [new ApiFieldError(field, message)];
}

internal sealed record ApiFieldError(string Field, string Message);
