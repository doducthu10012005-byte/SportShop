namespace SportShop.Api.Middleware;

internal class ApiFailure(int status, string code, string message, string? field = null,
    string fieldErrorSummary = "Dữ liệu không hợp lệ.") : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string? Field { get; } = field;
    public string FieldErrorSummary { get; } = fieldErrorSummary;
}
