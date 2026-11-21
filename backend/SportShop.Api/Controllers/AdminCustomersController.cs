using Microsoft.AspNetCore.Mvc;
using SportShop.Api.Admin;

namespace SportShop.Api.Controllers;

[Route("api/v1/admin/customers")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminCustomersController(CustomerData customers) : AdminController
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] CustomerQuery query, CancellationToken ct) =>
        Success(await customers.List(query, ct), message: "OK");

    [HttpGet("{userId:int}")]
    public async Task<IActionResult> Detail(int userId, CancellationToken ct) =>
        Success(await customers.Detail(userId, ct), message: "OK");

    [HttpPut("{userId:int}/status")]
    public async Task<IActionResult> Status(int userId, CustomerStatusInput input, CancellationToken ct) =>
        Success(await customers.SetStatus(userId, input.IsActive, ct));
}
