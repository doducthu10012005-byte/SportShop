using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SportShop.Api.Tests;

public sealed partial class CustomerOrderTests
{
    private const decimal TransferTotal = 30050m;
    private const string ClaimForPayment = "UPDATE dbo.Orders SET UpdatedAt=SYSUTCDATETIME()";

    private static Task<Reply> Confirm(Shop s, Actor? admin, int orderId, string? code = "FT-1", object? amount = null,
        string status = "Paid") => s.SendTo(admin, HttpMethod.Put, $"{AdminOrders}/{orderId}/payment",
            new { paymentStatus = status, transactionCode = code, receivedAmount = amount ?? TransferTotal });

    private static async Task<string> Snapshot(Shop s) => JsonSerializer.Serialize(new
    {
        Orders = await s.Db.Orders.OrderBy(o => o.OrderId).Select(o =>
            new { o.OrderId, o.StatusId, o.UpdatedAt, o.RowVersion, o.DeliveredAt, o.CancelReason }).ToListAsync(),
        Payments = await s.Db.Payments.OrderBy(p => p.PaymentId).Select(p => new
        {
            p.OrderId, p.PaymentId, p.PaymentStatus, p.Amount, p.ReceivedAmount, p.PaidAt,
            p.TransactionCode, p.ExpiresAt, p.UpdatedAt
        }).ToListAsync(),
        Inventory = await s.Db.Inventories.OrderBy(i => i.VariantId)
            .Select(i => new { i.VariantId, i.QuantityOnHand, i.UpdatedAt, i.RowVersion }).ToListAsync(),
        Ledger = await s.Db.InventoryTransactions.CountAsync(),
        History = await s.Db.OrderStatusHistories.CountAsync()
    });

    [SqlDevelopmentFact]
    public Task BankTransferValidatesRequestsAndReturnsPaidDetail() => Run(async s =>
    {
        EnableTestBank(s);
        var buyer = await s.Account("buyer");
        var admin = await s.Account("admin", "Admin");
        var variant = await s.Variant("sku", 50, 4);
        var id = await Buy(s, buyer, variant, 2);
        var cod = await Buy(s, buyer, variant);
        var before = await Snapshot(s);
        (await Confirm(s, null, id)).Error(401, "UNAUTHORIZED");
        (await Confirm(s, buyer, id)).Error(403, "FORBIDDEN");
        (await Confirm(s, admin, int.MaxValue)).Error(404, "NOT_FOUND");
        (await Confirm(s, admin, cod)).Error(409, "INVALID_STATUS_TRANSITION");
        foreach (var badCode in new[] { null, "   ", new string('x', 101) })
            (await Confirm(s, admin, id, badCode)).Error(400, "VALIDATION_ERROR");
        (await Confirm(s, admin, id, status: "Pending")).Error(400, "VALIDATION_ERROR");
        using var precise = JsonDocument.Parse("30050.0000000000000000000000000001");
        foreach (var amount in new object[] { -.01m, 30050.001m, "30050", precise.RootElement })
            (await Confirm(s, admin, id, amount: amount)).Error(400, "VALIDATION_ERROR");
        foreach (var amount in new[] { TransferTotal - .01m, TransferTotal + .01m })
        {
            var mismatch = await Confirm(s, admin, id, amount: amount);
            mismatch.Error(409, "PAYMENT_AMOUNT_MISMATCH");
            Assert.Equal("receivedAmount", mismatch.Body.GetProperty("errors")[0].GetProperty("field").GetString());
        }
        Assert.Equal(before, await Snapshot(s));

        var beforeTime = await s.Db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync();
        var detail = Success(await Confirm(s, admin, id, "  FT-1  "), "PENDING");
        var payment = detail.GetProperty("payment");
        Assert.Equal("Paid", payment.GetProperty("paymentStatus").GetString());
        Assert.Equal("FT-1", payment.GetProperty("transactionCode").GetString());
        Assert.Equal(TransferTotal, payment.GetProperty("receivedAmount").GetDecimal());
        var paid = await s.Db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == id);
        var afterTime = await s.Db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync();
        Assert.InRange(paid.PaidAt!.Value, beforeTime, afterTime);
        Assert.True(paid.PaidAt < paid.ExpiresAt);
        Assert.Equal(paid.PaidAt, paid.UpdatedAt);
        Assert.Equal(2, await s.Db.Inventories.Select(i => i.QuantityOnHand).SingleAsync());
        Assert.Equal(2, await s.Db.InventoryTransactions.CountAsync());
        Assert.Equal(1, await s.Db.OrderStatusHistories.CountAsync(h => h.OrderId == id));

        var another = await Buy(s, buyer, variant, 2);
        before = await Snapshot(s);
        foreach (var code in new[] { "FT-1", "ft-1" })
            (await Confirm(s, admin, another, code)).Error(409, "DUPLICATE_TRANSACTION");
        Assert.Equal(before, await Snapshot(s));
    });

    [SqlDevelopmentFact]
    public Task BankTransferIdempotencyAndShippedOrderPriority() => Run(async s =>
    {
        EnableTestBank(s);
        var buyer = await s.Account("buyer");
        var admin = await s.Account("admin", "Admin");
        var id = await Buy(s, buyer, await s.Variant("sku", 50, 2), 2);
        Success(await Confirm(s, admin, id), "PENDING");
        // A past deadline must not prevent replay of a successful payment.
        var paid = await s.Db.Payments.SingleAsync();
        var past = paid.CreatedAt.AddDays(-2);
        paid.CreatedAt = past;
        paid.PaidAt = past;
        paid.ExpiresAt = past.AddDays(1);
        await s.Db.SaveChangesAsync();
        foreach (var status in new[] { "PENDING", "CONFIRMED", "SHIPPING", "DELIVERED" })
        {
            if (status != "PENDING") Success(await Change(s, admin, id, status), status);
            var before = await Snapshot(s);
            var same = await Confirm(s, admin, id, " ft-1 ");
            if (status is "PENDING" or "CONFIRMED")
            {
                Success(same, status);
                (await Confirm(s, admin, id, "FT-NEW")).Error(409, "PAYMENT_ALREADY_CONFIRMED");
                (await Confirm(s, admin, id, amount: TransferTotal + 1)).Error(409, "PAYMENT_ALREADY_CONFIRMED");
            }
            else
            {
                same.Error(409, "INVALID_STATUS_TRANSITION");
                (await Confirm(s, admin, id, "FT-NEW", TransferTotal + 1)).Error(409, "INVALID_STATUS_TRANSITION");
            }
            Assert.Equal(before, await Snapshot(s));
        }
    });

    [SqlDevelopmentFact]
    public Task BankTransferRejectsExpiryAtUpdateBoundaryAndAfterDeadline() => Run(async s =>
    {
        EnableTestBank(s);
        var buyer = await s.Account("buyer");
        var admin = await s.Account("admin", "Admin");
        var id = await Buy(s, buyer, await s.Variant("sku", 50, 2), 2);
        var pay = await s.Db.Payments.SingleAsync();
        s.Gate.ConfirmationClock = pay.ExpiresAt;
        var before = await Snapshot(s);
        // The first read is within the deadline, then the update and retry see the exact deadline.
        (await Confirm(s, admin, id)).Error(409, "PAYMENT_EXPIRED");
        Assert.Equal(before, await Snapshot(s));
        s.Gate.ConfirmationClock = null;
        pay.CreatedAt = pay.CreatedAt.AddDays(-2);
        pay.ExpiresAt = pay.CreatedAt.AddDays(1);
        await s.Db.SaveChangesAsync();
        before = await Snapshot(s);
        (await Confirm(s, admin, id)).Error(409, "PAYMENT_EXPIRED");
        (await Confirm(s, admin, id, amount: 1)).Error(409, "PAYMENT_EXPIRED");
        Assert.Equal(before, await Snapshot(s));
        Success(await Cancel(s, buyer, id), "CANCELLED");
        (await Confirm(s, admin, id)).Error(409, "PAYMENT_EXPIRED");
    });

    [SqlDevelopmentFact]
    public Task ConcurrentBankTransferConfirmationIsIdempotent() => Run(async s =>
    {
        EnableTestBank(s);
        var buyer = await s.Account("buyer");
        var admin = await s.Account("admin", "Admin");
        var id = await Buy(s, buyer, await s.Variant("sku", 50, 2), 2);
        s.Gate.Arm(ClaimForPayment, 2);
        var requests = Task.WhenAll(Confirm(s, admin, id), Confirm(s, admin, id));
        try { await s.Gate.Wait(); } finally { s.Gate.Release(); }
        var payments = (await requests).Select(r => Success(r, "PENDING").GetProperty("payment")).ToArray();
        Assert.Equal(payments[0].GetProperty("paidAt").GetString(), payments[1].GetProperty("paidAt").GetString());
        Assert.Equal(1, await s.Db.Payments.CountAsync(p => p.OrderId == id && p.PaymentStatus == "Paid"));
    });

    [SqlDevelopmentFact]
    public Task BankTransferReevaluatesAfterCancellationOrShippingWins() => Run(async s =>
    {
        EnableTestBank(s);
        var buyer = await s.Account("buyer");
        var admin = await s.Account("admin", "Admin");
        var v = await s.Variant("sku", 50, 3);
        var cancelled = await Buy(s, buyer, v, 2);
        s.Gate.Arm(ClaimForPayment, 1);
        var losingPayment = Confirm(s, admin, cancelled);
        try
        {
            await s.Gate.Wait();
            Success(await Cancel(s, buyer, cancelled), "CANCELLED");
        }
        finally { s.Gate.Release(); }
        (await losingPayment).Error(409, "INVALID_STATUS_TRANSITION");
        Assert.Equal("Failed", await s.Db.Payments.Where(p => p.OrderId == cancelled).Select(p => p.PaymentStatus).SingleAsync());
        Assert.Equal(1, await s.Db.InventoryTransactions.CountAsync(t => t.TransactionType == "OrderRestock"));

        var shipped = await Buy(s, buyer, v, 2);
        Success(await Confirm(s, admin, shipped), "PENDING");
        Success(await Change(s, admin, shipped, "CONFIRMED"), "CONFIRMED");
        s.Gate.Arm(ClaimForPayment, 1);
        var replay = Confirm(s, admin, shipped, "different");
        string shippedSnapshot;
        try
        {
            await s.Gate.Wait();
            Success(await Change(s, admin, shipped, "SHIPPING"), "SHIPPING");
            shippedSnapshot = await Snapshot(s);
        }
        finally { s.Gate.Release(); }
        (await replay).Error(409, "INVALID_STATUS_TRANSITION");
        (await Confirm(s, admin, shipped, "different")).Error(409, "INVALID_STATUS_TRANSITION");
        Assert.Equal(shippedSnapshot, await Snapshot(s));
    });

    [SqlDevelopmentFact]
    public Task BankTransferReportsExhaustedOrderClaimsWithoutWriting() => Run(async s =>
    {
        EnableTestBank(s);
        var buyer = await s.Account("buyer");
        var admin = await s.Account("admin", "Admin");
        var id = await Buy(s, buyer, await s.Variant("sku", 50, 2), 2);
        var before = await Snapshot(s);

        s.Gate.ConflictsToSimulate = 5;
        (await Confirm(s, admin, id)).Error(409, "CONCURRENCY_CONFLICT");
        Assert.Equal(1, s.Gate.ConflictsToSimulate);
        Assert.Equal(before, await Snapshot(s));

        s.Gate.ConflictsToSimulate = 3;
        Success(await Confirm(s, admin, id), "PENDING");
        Assert.Equal(0, s.Gate.ConflictsToSimulate);
    });
}
