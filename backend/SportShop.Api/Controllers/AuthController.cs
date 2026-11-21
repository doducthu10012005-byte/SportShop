using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SportShop.Api.Auth;

namespace SportShop.Api.Controllers;

[ApiController, Route("api/v1/auth"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(AccountSessions sessions) : ControllerBase
{
    private const string CookieName = "refreshToken";

    [AllowAnonymous, HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest input, CancellationToken ct)
    {
        var user = await sessions.RegisterAsync(input, ct);
        return StatusCode(201, new { success = true, message = "Đăng ký thành công.",
            data = new { user.UserId, user.Email, user.FullName } });
    }

    [AllowAnonymous, HttpPost("login"), EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(LoginRequest input, CancellationToken ct) =>
        Issue(await sessions.LoginAsync(input, ct), "Đăng nhập thành công.");

    [AllowAnonymous, HttpPost("refresh"), CookieSession, EnableRateLimiting("auth")]
    public async Task<IActionResult> Refresh(CancellationToken ct) =>
        Issue(await sessions.RefreshAsync(Request.Cookies[CookieName], ct), "OK");

    [Authorize, HttpPost("logout"), CookieSession]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await sessions.LogoutAsync(int.Parse(User.FindFirst("sub")!.Value), Request.Cookies[CookieName], ct);
        var options = CookieOptions(TimeSpan.Zero);
        options.Expires = DateTimeOffset.UnixEpoch;
        Response.Cookies.Append(CookieName, "", options);
        return Ok(new { success = true, message = "Đã đăng xuất.", data = (object?)null });
    }

    private IActionResult Issue(IssuedSession result, string message)
    {
        Response.Cookies.Append(CookieName, result.Cookie, CookieOptions(result.Lifetime));
        return Ok(new { success = true, message, data = result.Body });
    }

    private static CookieOptions CookieOptions(TimeSpan maxAge) => new()
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/api/v1/auth", MaxAge = maxAge
    };
}
