using Microsoft.AspNetCore.Mvc;
using SportShop.Api.Admin;

namespace SportShop.Api.Controllers;

[Route("api/v1/admin/products")]
public sealed class AdminProductsController(ProductData products) : AdminController
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ProductQuery query, CancellationToken ct) =>
        Success(await products.List(query, ct));

    [HttpGet("{productId:int}")]
    public async Task<IActionResult> Detail(int productId, CancellationToken ct) =>
        Success(await products.Detail(productId, ct));

    [HttpPost]
    public async Task<IActionResult> Create(NewProduct input, CancellationToken ct) =>
        Success(await products.Save(null, input, ActorId, ct), 201);

    [HttpPut("{productId:int}")]
    public async Task<IActionResult> Update(int productId, ProductInput input, CancellationToken ct) =>
        Success(await products.Save(productId, input, ActorId, ct));

    [HttpDelete("{productId:int}")]
    public async Task<IActionResult> Delete(int productId, CancellationToken ct)
    {
        await products.Hide(productId, ct);
        return Success();
    }

    [HttpPost("{productId:int}/variants")]
    public async Task<IActionResult> AddVariant(int productId, NewVariant input, CancellationToken ct) =>
        Success(await products.CreateVariant(productId, input, ActorId, ct), 201);

    [HttpPut("{productId:int}/variants/{variantId:int}")]
    public async Task<IActionResult> UpdateVariant(int productId, int variantId, VariantInput input, CancellationToken ct) =>
        Success(await products.UpdateVariant(productId, variantId, input, ct));

    [HttpDelete("{productId:int}/variants/{variantId:int}")]
    public async Task<IActionResult> DeleteVariant(int productId, int variantId, CancellationToken ct)
    {
        await products.UpdateVariant(productId, variantId, null, ct);
        return Success();
    }
}
