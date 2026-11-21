using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;

namespace SportShop.Api.CustomerOrders;

internal sealed class BankTransferExpirySweep(IServiceScopeFactory scopes, ILogger<BankTransferExpirySweep> logger)
{
    private const int BatchSize = 50;
    private sealed record Candidate(int OrderId, int PaymentId, DateTime ExpiresAt);

    internal async Task Run(CancellationToken ct)
    {
        await using var scanScope = scopes.CreateAsyncScope();
        var db = scanScope.ServiceProvider.GetRequiredService<SportShopDbContext>();
        var cutoff = await db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync(ct);
        var lastExpiry = DateTime.MinValue;
        var lastId = 0;
        while (true)
        {
            var batch = await db.Database.SqlQuery<Candidate>($"""
                SELECT TOP ({BatchSize}) p.OrderId, p.PaymentId, p.ExpiresAt
                FROM dbo.Payments p WITH (INDEX(IX_Payments_Pending_ExpiresAt))
                JOIN dbo.PaymentMethods m ON m.PaymentMethodId=p.PaymentMethodId
                JOIN dbo.Orders o ON o.OrderId=p.OrderId
                JOIN dbo.OrderStatuses s ON s.StatusId=o.StatusId
                WHERE p.PaymentStatus='Pending' AND p.ExpiresAt IS NOT NULL AND p.ExpiresAt<= {cutoff}
                  AND m.MethodCode='BANK_TRANSFER' AND s.StatusCode IN ('PENDING','CONFIRMED')
                  AND p.ExpiresAt>= {lastExpiry} AND (p.ExpiresAt> {lastExpiry} OR p.PaymentId> {lastId})
                ORDER BY p.ExpiresAt, p.PaymentId
                """).ToListAsync(ct);
            foreach (var candidate in batch)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await using var orderScope = scopes.CreateAsyncScope();
                    var orders = orderScope.ServiceProvider.GetRequiredService<OrderTransitions>();
                    await orders.ExpireTransfer(candidate.OrderId, candidate.PaymentId, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    var cause = ex.GetBaseException();
                    logger.LogError("Bank transfer expiry failed for order {OrderId}, payment {PaymentId} ({ErrorType}, SQL error {SqlError}).",
                        candidate.OrderId, candidate.PaymentId, cause.GetType().Name, (cause as SqlException)?.Number);
                }
            }
            if (batch.Count < BatchSize) return;
            // Advance past skipped and failed candidates as well, so they cannot starve later batches.
            lastExpiry = batch[^1].ExpiresAt;
            lastId = batch[^1].PaymentId;
        }
    }
}
