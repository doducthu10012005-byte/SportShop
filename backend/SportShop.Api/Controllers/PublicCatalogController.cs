using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportShop.Api.PublicCatalog;

namespace SportShop.Api.Controllers;

[ApiController, AllowAnonymous, Route("api/v1")]
public sealed class PublicCatalogController(PublicProducts products, CatalogLookups lookups) : ControllerBase
{
    private IActionResult Success(object data) => Ok(new { success = true, message = "OK", data });

    [HttpGet("products")]
    public async Task<IActionResult> Products([FromQuery] CatalogQuery filter, CancellationToken ct) =>
        Success(await products.List(filter, ct));

    [HttpGet("products/{productId:int}")]
    public async Task<IActionResult> Detail(int productId, CancellationToken ct) =>
        Success(await products.Detail(productId, ct));

    [HttpGet("categories")]
    public async Task<IActionResult> Categories(CancellationToken ct) => Success(await lookups.Categories(ct));

    [HttpGet("brands")]
    public async Task<IActionResult> Brands(CancellationToken ct) => Success(await lookups.Brands(ct));

    [HttpGet("sizes")]
    public async Task<IActionResult> Sizes(CancellationToken ct) => Success(await lookups.Sizes(ct));

    [HttpGet("colors")]
    public async Task<IActionResult> Colors(CancellationToken ct) => Success(await lookups.Colors(ct));

    [HttpGet("payment-methods")]
    public async Task<IActionResult> PaymentMethods(CancellationToken ct) => Success(await lookups.PaymentMethods(ct));
}
