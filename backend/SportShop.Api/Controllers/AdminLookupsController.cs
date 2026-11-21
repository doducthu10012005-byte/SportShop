using Microsoft.AspNetCore.Mvc;
using SportShop.Api.Admin;

namespace SportShop.Api.Controllers;

[Route("api/v1/admin")]
public sealed class AdminLookupsController(LookupData data) : AdminController
{
    [HttpGet("brands")]
    public async Task<IActionResult> Brands([FromQuery] PageQuery page, CancellationToken ct) =>
        Success(await data.ListBrands(page, ct));

    [HttpPost("brands")]
    public async Task<IActionResult> CreateBrand(BrandInput input, CancellationToken ct) =>
        Success(await data.SaveBrand(null, input, ct), 201);

    [HttpPut("brands/{id:int}")]
    public async Task<IActionResult> UpdateBrand(int id, BrandInput input, CancellationToken ct) =>
        Success(await data.SaveBrand(id, input, ct));

    [HttpDelete("brands/{id:int}")]
    public async Task<IActionResult> DeleteBrand(int id, CancellationToken ct)
    {
        await data.HideBrand(id, ct);
        return Success();
    }

    [HttpGet("categories")]
    public async Task<IActionResult> Categories(CancellationToken ct) =>
        Success(await data.ListCategories(ct));

    [HttpPost("categories")]
    public async Task<IActionResult> CreateCategory(CategoryInput input, CancellationToken ct) =>
        Success(await data.SaveCategory(null, input, ct), 201);

    [HttpPut("categories/{id:int}")]
    public async Task<IActionResult> UpdateCategory(int id, CategoryInput input, CancellationToken ct) =>
        Success(await data.SaveCategory(id, input, ct));

    [HttpDelete("categories/{id:int}")]
    public async Task<IActionResult> DeleteCategory(int id, CancellationToken ct)
    {
        await data.HideCategory(id, ct);
        return Success();
    }

    [HttpGet("colors")]
    public async Task<IActionResult> Colors([FromQuery] PageQuery page, CancellationToken ct) =>
        Success(await data.ListColors(page, ct));

    [HttpPost("colors")]
    public async Task<IActionResult> CreateColor(ColorInput input, CancellationToken ct) =>
        Success(await data.SaveColor(null, input, ct), 201);

    [HttpPut("colors/{id:int}")]
    public async Task<IActionResult> UpdateColor(int id, ColorInput input, CancellationToken ct) =>
        Success(await data.SaveColor(id, input, ct));

    [HttpDelete("colors/{id:int}")]
    public async Task<IActionResult> DeleteColor(int id, CancellationToken ct)
    {
        await data.HideColor(id, ct);
        return Success();
    }

    [HttpGet("sizes")]
    public async Task<IActionResult> Sizes([FromQuery] PageQuery page, CancellationToken ct) =>
        Success(await data.ListSizes(page, ct));

    [HttpPost("sizes")]
    public async Task<IActionResult> CreateSize(SizeInput input, CancellationToken ct) =>
        Success(await data.SaveSize(null, input, ct), 201);

    [HttpPut("sizes/{id:int}")]
    public async Task<IActionResult> UpdateSize(int id, SizeInput input, CancellationToken ct) =>
        Success(await data.SaveSize(id, input, ct));

    [HttpDelete("sizes/{id:int}")]
    public async Task<IActionResult> DeleteSize(int id, CancellationToken ct)
    {
        await data.HideSize(id, ct);
        return Success();
    }
}
