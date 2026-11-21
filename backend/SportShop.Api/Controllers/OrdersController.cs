using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportShop.Api.CustomerOrders;

namespace SportShop.Api.Controllers;

[ApiController, Authorize(Policy = "Customer"), Route("api/v1/orders")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OrdersController(Checkout checkout, OrderReader reader, OrderTransitions transitions) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirst("sub")!.Value, CultureInfo.InvariantCulture);

    [HttpPost]
    public async Task<IActionResult> Place(CheckoutRequest input, CancellationToken ct) =>
        StatusCode(201, new { success = true, message = "Đặt hàng thành công.", data = await checkout.Place(UserId, input, ct) });

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] OrderListQuery filter, CancellationToken ct) =>
        Ok(new { success = true, message = "OK", data = await reader.List(UserId, filter, ct) });

    [HttpGet("{orderId:int}")]
    public async Task<IActionResult> Detail(int orderId, CancellationToken ct) =>
        Ok(new { success = true, message = "OK", data = await reader.Detail(UserId, orderId, ct) });

    [HttpPost("{orderId:int}/cancel")]
    public async Task<IActionResult> Cancel(int orderId, CancelOrderRequest input, CancellationToken ct) =>
        Ok(await transitions.CustomerCancel(UserId, orderId, input, ct));
}
