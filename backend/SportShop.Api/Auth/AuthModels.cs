using System.ComponentModel.DataAnnotations;

namespace SportShop.Api.Auth;

public sealed class RegisterRequest
{
    [Required, StringLength(100, MinimumLength = 2)] public string FullName { get; set; } = "";
    [Required, EmailAddress, MaxLength(256)] public string Email { get; set; } = "";
    [StrongPassword] public string Password { get; set; } = "";
    [Required, Compare(nameof(Password))] public string ConfirmPassword { get; set; } = "";
    [RegularExpression(@"(?:0|\+84)[0-9]{9}")] public string? PhoneNumber { get; set; }
}

public sealed class LoginRequest
{
    [Required, EmailAddress, MaxLength(256)] public string Email { get; set; } = "";
    [Required, MaxLength(64)] public string Password { get; set; } = "";
}

public sealed class ChangePasswordRequest
{
    [Required, MaxLength(64)] public string CurrentPassword { get; set; } = "";
    [StrongPassword] public string NewPassword { get; set; } = "";
    [Required, Compare(nameof(NewPassword))] public string ConfirmNewPassword { get; set; } = "";
}

public sealed class StrongPasswordAttribute : ValidationAttribute
{
    public override bool IsValid(object? value) => value is string p && p.Length is >= 8 and <= 64 &&
        p.Any(char.IsUpper) && p.Any(char.IsLower) && p.Any(char.IsDigit);
}

internal sealed record SessionUser(int UserId, string FullName, string Email, string Role, bool MustChangePassword);
internal sealed record SessionBody(string AccessToken, DateTime AccessTokenExpiresAt, SessionUser User);
internal sealed class IssuedSession(SessionBody body, string cookie, TimeSpan lifetime)
{
    public SessionBody Body { get; } = body;
    public string Cookie { get; } = cookie;
    public TimeSpan Lifetime { get; } = lifetime;
}

internal sealed class AuthFailure(int status, string code, string message, string? field = null)
    : Middleware.ApiFailure(status, code, message, field)
{
    public static AuthFailure InvalidSession() => new(401, "UNAUTHORIZED", "Phiên đăng nhập không hợp lệ.");
    public static AuthFailure InvalidCredentials() => new(401, "INVALID_CREDENTIALS", "Email hoặc mật khẩu không đúng.");
}
