using Microsoft.AspNetCore.Mvc;
using SportShop.Api.Admin;

namespace SportShop.Api.Controllers;

[Route("api/v1/admin/statistics")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminStatisticsController(RevenueStatistics statistics) : AdminController
{
    [HttpGet("revenue")]
    public async Task<IActionResult> Revenue([FromQuery] RevenueQuery query, CancellationToken ct) =>
        Success(await statistics.Read(query, ct), message: "OK");
}
