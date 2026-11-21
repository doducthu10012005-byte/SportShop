using Microsoft.EntityFrameworkCore;
using SportShop.Api.CustomerOrders;
using Xunit;

namespace SportShop.Api.Tests;

public sealed partial class CustomerOrderTests
{
    [SqlDevelopmentFact]
    public Task TransferAuditUsesAuthenticatedAdminAndOnlyLogsOnceAfterCommit()
    {
        var audit = new AuditLog<BankTransferConfirmation>();
        return Run(async shop =>
        {
            EnableTestBank(shop);
            var buyer = await shop.Account("buyer");
            var admin = await shop.Account("admin", "Admin");
            var adminId = await shop.Db.Users.Where(u => u.Email == "admin@example.test").Select(u => u.UserId).SingleAsync();
            var id = await Buy(shop, buyer, await shop.Variant("sku", 50, 3), 2);
            var before = await Snapshot(shop);
            shop.Gate.FailAfterTransferUpdate = true;
            (await Confirm(shop, admin, id)).Error(500, "INTERNAL_ERROR");
            Assert.Equal(before, await Snapshot(shop));
            Assert.Empty(audit.Entries);
            shop.Gate.FailAfterTransferUpdate = false;

            var request = new { paymentStatus = "Paid", transactionCode = "FT-1", receivedAmount = TransferTotal, actorId = int.MaxValue };
            shop.Gate.Arm(ClaimForPayment, 2);
            var confirmations = Task.WhenAll(
                shop.SendTo(admin, HttpMethod.Put, $"{AdminOrders}/{id}/payment", request),
                shop.SendTo(admin, HttpMethod.Put, $"{AdminOrders}/{id}/payment", request));
            try { await shop.Gate.Wait(); } finally { shop.Gate.Release(); }
            Assert.All(await confirmations, reply => Success(reply, "PENDING"));
            var settled = await Snapshot(shop);
            Success(await Confirm(shop, admin, id), "PENDING");
            (await Confirm(shop, admin, id, "FT-OTHER")).Error(409, "PAYMENT_ALREADY_CONFIRMED");
            Assert.Equal(settled, await Snapshot(shop));
            var item = Assert.Single(audit.Entries);
            var payment = await shop.Db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == id);
            Assert.Equal(adminId, Assert.IsType<int>(item["AdminId"]));
            Assert.Equal(id, Assert.IsType<int>(item["OrderId"]));
            Assert.Equal(await shop.Db.Orders.Where(o => o.OrderId == id).Select(o => o.OrderCode).SingleAsync(), item["OrderCode"]);
            Assert.Equal(payment.PaymentId, Assert.IsType<int>(item["PaymentId"]));
            var paidAt = Assert.IsType<DateTime>(item["PaidAtUtc"]);
            Assert.Equal(payment.PaidAt, paidAt);
            Assert.Equal(DateTimeKind.Utc, paidAt.Kind);
            Assert.Equal("Success", item["Result"]);
            Assert.Equal(7, item.Count); // Six structured fields plus the message template.
        }, transferLogger: audit);
    }

    private static Task<Reply> AdjustStock(Shop shop, Actor admin, int variantId, int quantity, string type = "Adjustment") =>
        shop.SendTo(admin, HttpMethod.Post, $"/api/v1/admin/inventory/{variantId}/adjustments",
            new { type, quantity, note = "Kiểm kê" });

    [SqlDevelopmentFact]
    public Task AdjustmentLeavesCapacityForCustomerCancellationAndExpiry() => Run(async shop =>
    {
        EnableTestBank(shop);
        var buyer = await shop.Account("buyer");
        var admin = await shop.Account("admin", "Admin");
        var variant = await shop.Variant("sku", 50, 10);
        var pending = await Buy(shop, buyer, variant, quantity: 2);
        var confirmed = await Buy(shop, buyer, variant, 2, quantity: 3);
        Success(await Change(shop, admin, confirmed, "CONFIRMED"), "CONFIRMED");
        var before = await Snapshot(shop);
        var rejected = await AdjustStock(shop, admin, variant.VariantId, int.MaxValue - 4);
        rejected.Error(400, "VALIDATION_ERROR");
        Assert.Equal("quantity", rejected.Body.GetProperty("errors")[0].GetProperty("field").GetString());
        (await AdjustStock(shop, admin, variant.VariantId, int.MaxValue - 9, "Import")).Error(400, "VALIDATION_ERROR");
        Assert.Equal(before, await Snapshot(shop));

        var adjusted = await AdjustStock(shop, admin, variant.VariantId, int.MaxValue - 5);
        Assert.Equal(200, adjusted.Status);
        Assert.Equal(int.MaxValue - 5, adjusted.Data.GetProperty("quantityOnHand").GetInt32());
        Success(await Cancel(shop, buyer, pending), "CANCELLED");
        Assert.Equal(int.MaxValue - 3, await shop.Db.Inventories.Select(i => i.QuantityOnHand).SingleAsync());
        await SetDeadline(shop, confirmed, DateTime.UtcNow.AddDays(-1));
        await shop.Sweep();
        await shop.Sweep();
        (await Cancel(shop, buyer, pending)).Error(409, "INVALID_STATUS_TRANSITION");
        Assert.Equal(int.MaxValue, await shop.Db.Inventories.Select(i => i.QuantityOnHand).SingleAsync());
        var restocks = await shop.Db.InventoryTransactions.Where(t => t.TransactionType == "OrderRestock")
            .OrderBy(t => t.TransactionId).ToListAsync();
        Assert.Equal(new[] { int.MaxValue - 3, int.MaxValue }, restocks.Select(t => t.QuantityAfter));
        Assert.Equal(new[] { 2, 3 }, restocks.Select(t => t.QuantityChange));
        Assert.Equal(2, restocks.Select(t => t.OrderItemId).Distinct().Count());
        Assert.Equal("Expired", await shop.Db.Payments.Where(p => p.OrderId == confirmed).Select(p => p.PaymentStatus).SingleAsync());
    });

    [SqlDevelopmentFact]
    public Task CheckoutInvalidatesConcurrentAdjustmentCapacityDecision() => Run(async shop =>
    {
        var buyer = await shop.Account("buyer");
        var admin = await shop.Account("admin", "Admin");
        var variant = await shop.Variant("sku", 50, 5);
        shop.Gate.Arm("UPDATE [Inventories]", 1);
        var adjust = AdjustStock(shop, admin, variant.VariantId, int.MaxValue);
        int id;
        try
        {
            await shop.Gate.Wait();
            id = await Buy(shop, buyer, variant, quantity: 2);
        }
        finally { shop.Gate.Release(); }
        (await adjust).Error(400, "VALIDATION_ERROR");
        Assert.Equal(3, await shop.Db.Inventories.Select(i => i.QuantityOnHand).SingleAsync());
        Assert.False(await shop.Db.InventoryTransactions.AnyAsync(t => t.TransactionType == "Adjustment"));
        Success(await Cancel(shop, buyer, id), "CANCELLED");
        Assert.Equal(5, await shop.Db.Inventories.Select(i => i.QuantityOnHand).SingleAsync());
        var nowAllowed = await AdjustStock(shop, admin, variant.VariantId, int.MaxValue);
        Assert.Equal(200, nowAllowed.Status);
        Assert.Equal(int.MaxValue, nowAllowed.Data.GetProperty("quantityOnHand").GetInt32());
    });
}
