using Microsoft.AspNetCore.Mvc;
using SportShop.Api.Admin;

namespace SportShop.Api.Controllers;

[Route("api/v1/admin/inventory")]
public sealed class AdminInventoryController(StockData stock) : AdminController
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] StockQuery query, CancellationToken ct) =>
        Success(await stock.List(query, ct));

    [HttpGet("{variantId:int}/transactions")]
    public async Task<IActionResult> History(int variantId, [FromQuery] PageQuery page, CancellationToken ct) =>
        Success(await stock.History(variantId, page, ct));

    [HttpPost("{variantId:int}/adjustments")]
    public async Task<IActionResult> Adjust(int variantId, StockInput input, CancellationToken ct) =>
        Success(await stock.Adjust(variantId, input, ActorId, ct));
}
