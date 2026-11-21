using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SportShop.Api.CustomerOrders;
using SportShop.Api.Data.Entities;
using Xunit;

namespace SportShop.Api.Tests;

public sealed partial class CustomerOrderTests
{
    private const string RefundedPreviously = "Khoản thanh toán đã được xác nhận hoàn tiền trước đó.";

    private sealed class AuditLog<T> : ILogger<T>
    {
        public ConcurrentQueue<IReadOnlyDictionary<string, object?>> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel) && state is IEnumerable<KeyValuePair<string, object?>> values)
                Entries.Enqueue(values.ToDictionary(entry => entry.Key, entry => entry.Value));
        }
    }

    private static Task<Reply> Refund(Shop shop, Actor? admin, int orderId, object? input = null) =>
        shop.SendTo(admin, HttpMethod.Put, $"{AdminOrders}/{orderId}/refund", input ?? new { refundCompleted = true });

    private static async Task<int> PrepareRefund(Shop shop, Actor buyer, Actor admin, ProductVariant variant)
    {
        var id = await Buy(shop, buyer, variant, 2);
        Success(await Confirm(shop, admin, id), "PENDING");
        var cancelled = Success(await Cancel(shop, buyer, id), "CANCELLED");
        Assert.Equal("RefundPending", cancelled.GetProperty("payment").GetProperty("paymentStatus").GetString());
        return id;
    }

    private static Task<Payment> RefundPayment(Shop shop, int orderId) =>
        shop.Db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == orderId);

    [SqlDevelopmentFact]
    public Task RefundRequiresAdminAndExplicitConfirmationThenRecordsOnlyPayment()
    {
        var audit = new AuditLog<RefundConfirmation>();
        return Run(async shop =>
        {
            EnableTestBank(shop);
            var buyer = await shop.Account("buyer");
            var admin = await shop.Account("admin", "Admin");
            var id = await PrepareRefund(shop, buyer, admin, await shop.Variant("sku", 50, 2));
            var unchanged = await Snapshot(shop);
            (await Refund(shop, null, id)).Error(401, "UNAUTHORIZED");
            (await Refund(shop, buyer, id)).Error(403, "FORBIDDEN");
            (await Refund(shop, admin, int.MaxValue)).Error(404, "NOT_FOUND");
            foreach (var body in new object[] { new { }, new { refundCompleted = false }, new { refundCompleted = "true" } })
                (await Refund(shop, admin, id, body)).Error(400, "VALIDATION_ERROR");
            Assert.Equal(unchanged, await Snapshot(shop));
            Assert.Empty(audit.Entries);

            var original = await RefundPayment(shop, id);
            var rowVersion = await shop.Db.Orders.Where(o => o.OrderId == id).Select(o => o.RowVersion).SingleAsync();
            var start = await shop.Db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync();
            var response = Success(await Refund(shop, admin, id), "CANCELLED").GetProperty("payment");
            var completed = await RefundPayment(shop, id);
            var end = await shop.Db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync();
            Assert.Equal("Refunded", response.GetProperty("paymentStatus").GetString());
            Assert.InRange(completed.RefundedAt!.Value, start, end);
            Assert.Equal(completed.RefundedAt, completed.UpdatedAt);
            Assert.Equal(completed.RefundedAt, response.GetProperty("refundedAt").GetDateTime());
            Assert.Equal(original.Amount, completed.Amount);
            Assert.Equal(original.ReceivedAmount, completed.ReceivedAmount);
            Assert.Equal(original.PaidAt, completed.PaidAt);
            Assert.Equal(original.ExpiresAt, completed.ExpiresAt);
            Assert.Equal(original.TransactionCode, completed.TransactionCode);
            Assert.Equal(rowVersion, await shop.Db.Orders.Where(o => o.OrderId == id).Select(o => o.RowVersion).SingleAsync());
            Assert.Equal(2, await shop.Db.Inventories.Select(i => i.QuantityOnHand).SingleAsync());
            Assert.Equal(2, await shop.Db.InventoryTransactions.CountAsync());
            Assert.Equal(2, await shop.Db.OrderStatusHistories.CountAsync());
            var entry = Assert.Single(audit.Entries);
            Assert.Equal(await shop.Db.Users.Where(u => u.Email == "admin@example.test").Select(u => u.UserId).SingleAsync(),
                Assert.IsType<int>(entry["AdminId"]));
            Assert.Equal(id, Assert.IsType<int>(entry["OrderId"]));
            Assert.Equal(await shop.Db.Orders.Where(o => o.OrderId == id).Select(o => o.OrderCode).SingleAsync(), entry["OrderCode"]);
            Assert.Equal("Success", entry["Result"]);
            Assert.Equal(completed.PaymentId, Assert.IsType<int>(entry["PaymentId"]));
            Assert.Equal(completed.RefundedAt, Assert.IsType<DateTime>(entry["RefundedAtUtc"]));

            var settled = await Snapshot(shop);
            var repeat = await Refund(shop, admin, id);
            Success(repeat, "CANCELLED");
            Assert.Equal(RefundedPreviously, repeat.Body.GetProperty("message").GetString());
            Assert.Equal(settled, await Snapshot(shop));
            Assert.Single(audit.Entries);
        }, audit);
    }

    [SqlDevelopmentFact]
    public Task ConcurrentRefundsWriteAndAuditExactlyOnce()
    {
        var audit = new AuditLog<RefundConfirmation>();
        return Run(async shop =>
        {
            EnableTestBank(shop);
            var buyer = await shop.Account("buyer");
            var admin = await shop.Account("admin", "Admin");
            var second = await shop.Account("second", "Admin");
            var id = await PrepareRefund(shop, buyer, admin, await shop.Variant("sku", 50, 2));
            shop.Gate.Arm("SET PaymentStatus='Refunded'", 2);
            var refunds = Task.WhenAll(Refund(shop, admin, id), Refund(shop, second, id));
            try { await shop.Gate.Wait(); } finally { shop.Gate.Release(); }
            var replies = await refunds;
            var timestamps = replies.Select(r => Success(r, "CANCELLED").GetProperty("payment").GetProperty("refundedAt").GetString());
            Assert.Single(timestamps.Distinct());
            Assert.Single(replies, r => r.Body.GetProperty("message").GetString() == RefundedPreviously);
            Assert.Single(audit.Entries);
            Assert.Equal(1, await shop.Db.Payments.CountAsync(p => p.OrderId == id && p.PaymentStatus == "Refunded"));
            Assert.Equal(1, await shop.Db.InventoryTransactions.CountAsync(t => t.TransactionType == "OrderRestock"));
        }, audit);
    }

    [SqlDevelopmentFact]
    public Task RefundRejectsUnsettledAndUncancelledPayments() => Run(async shop =>
    {
        EnableTestBank(shop);
        var buyer = await shop.Account("buyer");
        var admin = await shop.Account("admin", "Admin");
        var variant = await shop.Variant("sku", 50, 4);
        var pending = await Buy(shop, buyer, variant, 2);
        var paid = await Buy(shop, buyer, variant, 2);
        Success(await Confirm(shop, admin, paid), "PENDING");
        var failed = await Buy(shop, buyer, variant);
        Success(await Cancel(shop, buyer, failed), "CANCELLED");
        var expired = await Buy(shop, buyer, variant, 2);
        await SetDeadline(shop, expired, DateTime.UtcNow.AddDays(-1));
        await shop.Sweep();
        var before = await Snapshot(shop);
        foreach (var id in new[] { pending, paid, failed, expired })
            (await Refund(shop, admin, id)).Error(409, "INVALID_STATUS_TRANSITION");
        Assert.Equal(new[] { "Pending", "Paid", "Failed", "Expired" }, await shop.Db.Payments
            .OrderBy(p => p.OrderId).Select(p => p.PaymentStatus).ToArrayAsync());
        Assert.Equal(before, await Snapshot(shop));
    });

    [SqlDevelopmentFact]
    public Task SqlFailureAfterRefundUpdateRollsBackAndDoesNotAudit()
    {
        var audit = new AuditLog<RefundConfirmation>();
        return Run(async shop =>
        {
            EnableTestBank(shop);
            var buyer = await shop.Account("buyer");
            var admin = await shop.Account("admin", "Admin");
            var id = await PrepareRefund(shop, buyer, admin, await shop.Variant("sku", 50, 2));
            var before = await Snapshot(shop);
            shop.Gate.FailAfterRefundUpdate = true;
            (await Refund(shop, admin, id)).Error(500, "INTERNAL_ERROR");
            Assert.Equal(before, await Snapshot(shop));
            Assert.Null((await RefundPayment(shop, id)).RefundedAt);
            Assert.Empty(audit.Entries);
            shop.Gate.FailAfterRefundUpdate = false;
            Success(await Refund(shop, admin, id), "CANCELLED");
            Assert.Single(audit.Entries);
        }, audit);
    }

    [SqlDevelopmentFact]
    public Task ReconcileDeliveredPaymentsAndCancelledInventory() => Run(async shop =>
    {
        EnableTestBank(shop);
        var buyer = await shop.Account("buyer");
        var admin = await shop.Account("admin", "Admin");
        var shoes = await shop.Variant("shoes", 50, 6);
        var shirt = await shop.Variant("shirt", 100, 5);
        var delivered = new List<int>();
        foreach (var method in new[] { 1, 2 })
        {
            var id = await Buy(shop, buyer, shoes, method);
            if (method == 2) Success(await Confirm(shop, admin, id, "FT-DELIVER"), "PENDING");
            foreach (var status in new[] { "CONFIRMED", "SHIPPING", "DELIVERED" })
                Success(await Change(shop, admin, id, status), status);
            delivered.Add(id);
        }

        await shop.Fill(buyer, shirt);
        var cancelled = await Buy(shop, buyer, shoes, 2, quantity: 2);
        Success(await Confirm(shop, admin, cancelled, "FT-RETURN", 30200m), "PENDING");
        Success(await Cancel(shop, buyer, cancelled), "CANCELLED");
        Success(await Refund(shop, admin, cancelled), "CANCELLED");
        Success(await Refund(shop, admin, cancelled), "CANCELLED");
        (await Cancel(shop, buyer, cancelled)).Error(409, "INVALID_STATUS_TRANSITION");

        var orders = await shop.Db.Orders.AsNoTracking().Include(o => o.Status).Include(o => o.Items)
            .Include(o => o.Payments).AsSplitQuery().ToListAsync();
        foreach (var order in orders)
        {
            var payment = Assert.Single(order.Payments);
            Assert.Equal(order.Items.Sum(i => i.LineTotal), order.Subtotal);
            Assert.Equal(order.Subtotal + order.ShippingFee, order.TotalAmount);
            Assert.Equal(order.TotalAmount, payment.Amount);
            Assert.Equal(payment.Amount, payment.ReceivedAmount);
            Assert.NotNull(payment.PaidAt);
            var isDelivered = delivered.Contains(order.OrderId);
            Assert.Equal(isDelivered ? "DELIVERED" : "CANCELLED", order.Status.StatusCode);
            Assert.Equal(isDelivered ? "Paid" : "Refunded", payment.PaymentStatus);
            foreach (var item in order.Items)
            {
                var events = await shop.Db.InventoryTransactions.Where(t => t.OrderItemId == item.OrderItemId).ToListAsync();
                Assert.Equal(-item.Quantity, Assert.Single(events, e => e.TransactionType == "Sale").QuantityChange);
                if (isDelivered) Assert.Single(events);
                else
                {
                    Assert.Equal(2, events.Count);
                    Assert.Equal(item.Quantity, Assert.Single(events, e => e.TransactionType == "OrderRestock").QuantityChange);
                }
            }
        }
        foreach (var (variant, initial) in new[] { (shoes, 6), (shirt, 5) })
        {
            var movements = await shop.Db.InventoryTransactions.Where(t => t.VariantId == variant.VariantId)
                .OrderBy(t => t.TransactionId).ToListAsync();
            var balance = initial;
            foreach (var movement in movements)
            {
                balance += movement.QuantityChange;
                Assert.Equal(balance, movement.QuantityAfter);
            }
            Assert.Equal(balance, await shop.Db.Inventories.Where(i => i.VariantId == variant.VariantId)
                .Select(i => i.QuantityOnHand).SingleAsync());
        }
    });
}
