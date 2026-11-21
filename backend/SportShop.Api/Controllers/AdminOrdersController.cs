using Microsoft.AspNetCore.Mvc;
using SportShop.Api.Admin;
using SportShop.Api.CustomerOrders;

namespace SportShop.Api.Controllers;

[Route("api/v1/admin/orders")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminOrdersController(OrderReader reader, OrderTransitions transitions) : AdminController
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] AdminOrderQuery query, CancellationToken ct) =>
        Success(await reader.List(null, query, ct), message: "OK");

    [HttpGet("{orderId:int}")]
    public async Task<IActionResult> Detail(int orderId, CancellationToken ct) =>
        Success(await reader.Detail(null, orderId, ct), message: "OK");

    [HttpPut("{orderId:int}/status")]
    public async Task<IActionResult> Change(int orderId, ChangeStatusRequest input, CancellationToken ct) =>
        Ok(await transitions.AdminChange(ActorId, orderId, input, ct));
}
