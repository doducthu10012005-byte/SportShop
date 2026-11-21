using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportShop.Api.Auth;

namespace SportShop.Api.Controllers;

[ApiController, Authorize, Route("api/v1/users/me"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CurrentUserController(CurrentUserData data, AccountSessions sessions) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirst("sub")!.Value);

    [HttpGet]
    public async Task<IActionResult> Me(CancellationToken ct) => Success(await data.Profile(UserId, ct));

    [HttpPut]
    public async Task<IActionResult> Update(ProfileInput input, CancellationToken ct) =>
        Success(await data.UpdateProfile(UserId, input, ct));

    [HttpGet("addresses")]
    public async Task<IActionResult> Addresses([FromQuery] AddressQuery query, CancellationToken ct) =>
        Success(await data.Addresses(UserId, query, ct));

    [HttpPost("addresses")]
    public async Task<IActionResult> AddAddress(AddressInput input, CancellationToken ct) =>
        StatusCode(201, new { success = true, message = "Đã thêm địa chỉ.", data = await data.SaveAddress(UserId, null, input, ct) });

    [HttpPut("addresses/{addressId:int}")]
    public async Task<IActionResult> UpdateAddress(int addressId, AddressInput input, CancellationToken ct) =>
        Success(await data.SaveAddress(UserId, addressId, input, ct));

    [HttpDelete("addresses/{addressId:int}")]
    public async Task<IActionResult> DeleteAddress(int addressId, CancellationToken ct) =>
        Success(await data.DeleteAddress(UserId, addressId, ct));

    private IActionResult Success(object? value) => Ok(new { success = true, message = "OK", data = value });

    [HttpPut("password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest input, CancellationToken ct)
    {
        await sessions.ChangeAsync(UserId, int.Parse(User.FindFirst("tv")!.Value), input, ct);
        return Ok(new { success = true, message = "Đổi mật khẩu thành công. Vui lòng đăng nhập lại.", data = (object?)null });
    }
}
