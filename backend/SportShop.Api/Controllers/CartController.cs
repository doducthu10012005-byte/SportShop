using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportShop.Api.ShoppingCart;

namespace SportShop.Api.Controllers;

[ApiController, Authorize(Policy = "Customer"), Route("api/v1/cart")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CartController(CustomerCart cart) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirst("sub")!.Value, CultureInfo.InvariantCulture);
    private IActionResult Success(CartView data) => Ok(new { success = true, message = "OK", data });

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) => Success(await cart.Read(UserId, ct));

    [HttpPost("items")]
    public async Task<IActionResult> Add(AddCartItem input, CancellationToken ct) =>
        Success(await cart.Add(UserId, input, ct));

    [HttpPut("items/{cartItemId:int}")]
    public async Task<IActionResult> SetQuantity(int cartItemId, SetCartQuantity input, CancellationToken ct) =>
        Success(await cart.SetQuantity(UserId, cartItemId, input.Quantity, ct));

    [HttpDelete("items/{cartItemId:int}")]
    public async Task<IActionResult> Remove(int cartItemId, CancellationToken ct) =>
        Success(await cart.Remove(UserId, cartItemId, ct));

    [HttpDelete]
    public async Task<IActionResult> Clear(CancellationToken ct) => Success(await cart.Clear(UserId, ct));
}
