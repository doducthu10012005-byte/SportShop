using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Auth;
using SportShop.Api.Data;

namespace SportShop.Api.Controllers;

[ApiController, Authorize, Route("api/v1/users/me"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CurrentUserController(SportShopDbContext db, AccountSessions sessions) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirst("sub")!.Value);

    [HttpGet]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var profile = await db.Users.AsNoTracking().Where(u => u.UserId == UserId)
            .Select(u => new { u.UserId, u.Email, u.FullName, u.PhoneNumber, u.DateOfBirth, u.Gender, role = u.Role.RoleName })
            .SingleOrDefaultAsync(ct) ?? throw AuthFailure.InvalidSession();
        return Ok(new { success = true, message = "OK", data = profile });
    }

    [HttpPut("password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest input, CancellationToken ct)
    {
        await sessions.ChangeAsync(UserId, int.Parse(User.FindFirst("tv")!.Value), input, ct);
        return Ok(new { success = true, message = "Đổi mật khẩu thành công. Vui lòng đăng nhập lại.", data = (object?)null });
    }
}
