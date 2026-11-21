using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SportShop.Api.Tests;

public sealed partial class CustomerOrderTests
{
    private const string ExpiryReason = "Hết hạn thanh toán chuyển khoản";
    private const string ClaimForExpiry = "UPDATE dbo.Orders SET StatusId";

    private static async Task SetDeadline(Shop shop, int orderId, DateTime deadline)
    {
        await shop.Db.Payments.Where(p => p.OrderId == orderId).ExecuteUpdateAsync(update => update
            .SetProperty(p => p.CreatedAt, deadline.AddDays(-1)).SetProperty(p => p.ExpiresAt, deadline));
    }

    private static async Task AssertExpired(Shop shop, int orderId)
    {
        var order = await shop.Db.Orders.Where(o => o.OrderId == orderId)
            .Select(o => new { o.CancelReason, o.Status.StatusCode }).SingleAsync();
        Assert.Equal("CANCELLED", order.StatusCode);
        Assert.Equal(ExpiryReason, order.CancelReason);
        Assert.Equal("Expired", await shop.Db.Payments.Where(p => p.OrderId == orderId).Select(p => p.PaymentStatus).SingleAsync());
        var history = await shop.Db.OrderStatusHistories.Where(h => h.OrderId == orderId && h.ToStatus.StatusCode == "CANCELLED").SingleAsync();
        Assert.Equal(ExpiryReason, history.Note);
        Assert.Null(history.ChangedBy);
        var restock = await shop.Db.InventoryTransactions.Where(t => t.OrderItem!.OrderId == orderId &&
            t.TransactionType == "OrderRestock").SingleAsync();
        Assert.Equal(ExpiryReason, restock.Note);
        Assert.Null(restock.CreatedBy);
        Assert.Equal(1, restock.QuantityChange);
        Assert.NotNull(restock.OrderItemId);
    }

    [SqlDevelopmentFact]
    public Task SweepExpiresAtInclusiveBoundaryAndSkipsIneligiblePayments() => Run(async s =>
    {
        EnableTestBank(s);
        var buyer = await s.Account("buyer");
        var admin = await s.Account("admin", "Admin");
        var variant = await s.Variant("sku", 50, 10);
        var boundary = await Buy(s, buyer, variant, 2);
        var overdue = await Buy(s, buyer, variant, 2);
        var future = await Buy(s, buyer, variant, 2);
        var cod = await Buy(s, buyer, variant);
        var paid = await Buy(s, buyer, variant, 2);
        Success(await Confirm(s, admin, paid), "PENDING");
        Success(await Change(s, admin, overdue, "CONFIRMED"), "CONFIRMED");
        var cutoff = await s.Db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync();
        await SetDeadline(s, boundary, cutoff);
        await SetDeadline(s, overdue, cutoff.AddTicks(-1));
        await SetDeadline(s, future, cutoff.AddTicks(1));
        // An intentionally stale candidate must also be rejected after the worker acquires the order.
        var snapshot = await Snapshot(s);
        var paidId = await s.Db.Payments.Where(p => p.OrderId == paid).Select(p => p.PaymentId).SingleAsync();
        await s.ExpireCandidate(paid, paidId);
        Assert.Equal(snapshot, await Snapshot(s));

        s.Gate.ExpiryClock = cutoff;
        await s.Sweep();
        await AssertExpired(s, boundary);
        await AssertExpired(s, overdue);
        Assert.Equal(new[] { "Pending", "Pending", "Paid" }, await s.Db.Payments
            .Where(p => p.OrderId == future || p.OrderId == cod || p.OrderId == paid).OrderBy(p => p.OrderId)
            .Select(p => p.PaymentStatus).ToArrayAsync());
        Assert.Equal(7, await s.Db.Inventories.Select(i => i.QuantityOnHand).SingleAsync());
        snapshot = await Snapshot(s);
        await s.Sweep();
        Assert.Equal(snapshot, await Snapshot(s));
    });

    [SqlDevelopmentFact]
    public Task TwoSweepsSelectingSameOrderRestockOnlyOnce() => Run(async s =>
    {
        EnableTestBank(s);
        var buyer = await s.Account("buyer");
        var id = await Buy(s, buyer, await s.Variant("sku", 50, 2), 2);
        await SetDeadline(s, id, DateTime.UtcNow.AddDays(-1));
        s.Gate.Arm(ClaimForExpiry, 2);
        var sweeps = Task.WhenAll(s.Sweep(), s.Sweep());
        try { await s.Gate.Wait(); } finally { s.Gate.Release(); }
        await sweeps;
        await AssertExpired(s, id);
        Assert.Equal(2, await s.Db.Inventories.Select(i => i.QuantityOnHand).SingleAsync());
        Assert.Equal(2, await s.Db.OrderStatusHistories.CountAsync());
    });

    [SqlDevelopmentFact]
    public Task SweepSerializesWithPaymentConfirmationAndUserCancellations() => Run(async s =>
    {
        EnableTestBank(s);
        var buyer = await s.Account("buyer");
        var admin = await s.Account("admin", "Admin");
        var variant = await s.Variant("sku", 50, 3);
        var id = await Buy(s, buyer, variant, 2);
        await SetDeadline(s, id, DateTime.UtcNow.AddDays(-1));
        s.Gate.Arm(ClaimForPayment, 1);
        var lateConfirmation = Confirm(s, admin, id);
        try
        {
            await s.Gate.Wait();
            await s.Sweep();
        }
        finally { s.Gate.Release(); }
        (await lateConfirmation).Error(409, "PAYMENT_EXPIRED");
        await AssertExpired(s, id);

        foreach (var isAdmin in new[] { false, true })
        {
            var cancelled = await Buy(s, buyer, variant, 2);
            await SetDeadline(s, cancelled, DateTime.UtcNow.AddDays(-1));
            s.Gate.Arm(ClaimForExpiry, 1);
            var sweep = s.Sweep();
            try
            {
                await s.Gate.Wait();
                var reply = isAdmin ? await Change(s, admin, cancelled, "CANCELLED") : await Cancel(s, buyer, cancelled);
                Assert.Equal("Expired", Success(reply, "CANCELLED").GetProperty("payment").GetProperty("paymentStatus").GetString());
            }
            finally { s.Gate.Release(); }
            await sweep;
            Assert.Equal(1, await s.Db.InventoryTransactions.CountAsync(t => t.OrderItem!.OrderId == cancelled &&
                t.TransactionType == "OrderRestock" && t.CreatedBy != null));
            Assert.Equal(2, await s.Db.OrderStatusHistories.CountAsync(h => h.OrderId == cancelled));
        }
        Assert.Equal(3, await s.Db.Inventories.Select(i => i.QuantityOnHand).SingleAsync());
    });

    [SqlDevelopmentFact]
    public Task SweepRollsBackBothKindsOfFailureAndContinuesAcrossBatches() => Run(async s =>
    {
        EnableTestBank(s);
        var buyer = await s.Account("buyer");
        var badStock = await s.Variant("broken", 50, 2);
        var goodStock = await s.Variant("good", 50, 52);
        var failing = await Buy(s, buyer, badStock, 2);
        var deadline = DateTime.UtcNow.AddDays(-1);
        await SetDeadline(s, failing, deadline);
        var before = await Snapshot(s);
        s.Gate.FailPaymentUpdate = true;
        await s.Sweep();
        s.Gate.FailPaymentUpdate = false;
        Assert.Equal(before, await Snapshot(s));

        // Force an actual SQL overflow in replenishment after updating the payment.
        await s.Db.Inventories.Where(i => i.VariantId == badStock.VariantId)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.QuantityOnHand, int.MaxValue));
        var originalVersion = await s.Db.Orders.Where(o => o.OrderId == failing).Select(o => o.RowVersion).SingleAsync();
        for (var i = 0; i < 50; i++)
        {
            var id = await Buy(s, buyer, goodStock, 2);
            await SetDeadline(s, id, deadline);
        }
        await s.Sweep();
        Assert.Equal(50, await s.Db.Payments.CountAsync(p => p.PaymentStatus == "Expired"));
        Assert.Equal(50, await s.Db.InventoryTransactions.CountAsync(t => t.TransactionType == "OrderRestock"));
        Assert.Equal(52, await s.Db.Inventories.Where(i => i.VariantId == goodStock.VariantId).Select(i => i.QuantityOnHand).SingleAsync());
        Assert.Equal("Pending", await s.Db.Payments.Where(p => p.OrderId == failing).Select(p => p.PaymentStatus).SingleAsync());
        Assert.Equal(originalVersion, await s.Db.Orders.Where(o => o.OrderId == failing).Select(o => o.RowVersion).SingleAsync());
        Assert.Equal(1, await s.Db.OrderStatusHistories.CountAsync(h => h.OrderId == failing));
        var settled = await Snapshot(s);
        await s.Sweep();
        Assert.Equal(settled, await Snapshot(s));
    });
}
