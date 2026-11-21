using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data.Entities;
using Xunit;

namespace SportShop.Api.Tests;

public sealed partial class CustomerOrderTests
{
    private const string AdminOrders = "/api/v1/admin/orders";
    private const string Reason = "Khách yêu cầu hủy";

    private static async Task<int> Buy(Shop s, Actor buyer, ProductVariant variant, int method = 1, int quantity = 1)
    {
        await s.Fill(buyer, variant, quantity);
        var reply = await s.Place(buyer, method);
        Assert.Equal(201, reply.Status);
        return reply.Data.GetProperty("orderId").GetInt32();
    }

    private static Task<Reply> Cancel(Shop s, Actor? buyer, int id, string? reason = Reason) =>
        s.Send(buyer, HttpMethod.Post, $"/{id}/cancel", new { cancelReason = reason });

    private static Task<Reply> Change(Shop s, Actor? admin, int id, string status, string? reason = Reason) =>
        s.SendTo(admin, HttpMethod.Put, $"{AdminOrders}/{id}/status", new { statusCode = status, cancelReason = reason, note = "Ghi chú" });

    private static JsonElement Success(Reply r, string status)
    {
        Assert.Equal(200, r.Status);
        Assert.Equal(status, r.Data.GetProperty("status").GetProperty("statusCode").GetString());
        return r.Data;
    }

    private static void EnableTestBank(Shop s)
    {
        s.Config["Payment:BankAccountNumber"] = "00001000";
        s.Config["Payment:BankName"] = "Test Bank";
    }

    [SqlDevelopmentFact]
    public Task CustomerCancellationChecksOwnerReasonAndStockLedger() => Run(async s =>
    {
        var buyer = await s.Account("buyer");
        var other = await s.Account("other");
        var admin = await s.Account("admin", "Admin");
        var first = await s.Variant("first", 80, 5);
        var second = await s.Variant("second", 70, 2);
        await s.Fill(buyer, second);
        var id = await Buy(s, buyer, first, quantity: 2);
        (await Cancel(s, null, id)).Error(401, "UNAUTHORIZED");
        (await Cancel(s, admin, id)).Error(403, "FORBIDDEN");
        (await Cancel(s, other, id)).Error(404, "NOT_FOUND");
        foreach (var reason in new[] { null, "   ", " 1234 ", new string('x', 256) })
            (await Cancel(s, buyer, id, reason)).Error(400, "VALIDATION_ERROR");
        var data = Success(await Cancel(s, buyer, id, " " + Reason + " "), "CANCELLED");
        Assert.Equal(Reason, data.GetProperty("cancelReason").GetString());
        var pay = data.GetProperty("payment");
        Assert.Equal("Failed", pay.GetProperty("paymentStatus").GetString());
        Assert.False(pay.TryGetProperty("transactionCode", out _));
        Assert.False(data.TryGetProperty("customer", out _));
        var history = data.GetProperty("statusHistory");
        Assert.Equal(2, history.GetArrayLength());
        Assert.Equal(Reason, history[1].GetProperty("note").GetString());
        (await Cancel(s, buyer, id)).Error(409, "INVALID_STATUS_TRANSITION");
        (await Change(s, admin, id, "CANCELLED")).Error(409, "INVALID_STATUS_TRANSITION");
        var lines = await s.Db.OrderItems.OrderBy(i => i.VariantId).ToListAsync();
        var restocks = await s.Db.InventoryTransactions.Where(t => t.TransactionType == "OrderRestock")
            .OrderBy(t => t.VariantId).ToListAsync();
        Assert.Equal(lines.Select(i => (int?)i.OrderItemId), restocks.Select(t => t.OrderItemId));
        Assert.Equal(lines.Select(i => i.Quantity), restocks.Select(t => t.QuantityChange));
        Assert.Equal(new[] { 5, 2 }, restocks.Select(t => t.QuantityAfter));
        Assert.Equal(await s.Db.Inventories.OrderBy(i => i.VariantId).Select(i => i.QuantityOnHand).ToListAsync(),
            restocks.Select(t => t.QuantityAfter));
        var actorId = await s.Db.Carts.Where(c => c.CartId == buyer.CartId).Select(c => c.UserId).SingleAsync();
        Assert.All(restocks, r => Assert.Equal(actorId, r.CreatedBy));
        Assert.Equal(actorId, await s.Db.OrderStatusHistories.Where(h => h.FromStatusId != null).Select(h => h.ChangedBy).SingleAsync());
    });

    [SqlDevelopmentFact]
    public Task AdminTransitionsCollectCodOnlyOnDelivery() => Run(async s =>
    {
        var buyer = await s.Account("buyer");
        var admin = await s.Account("admin", "Admin");
        var id = await Buy(s, buyer, await s.Variant("boots", 50, 3));
        foreach (var actor in new Actor?[] { null, buyer })
        {
            var status = actor == null ? 401 : 403;
            var code = actor == null ? "UNAUTHORIZED" : "FORBIDDEN";
            (await Change(s, actor, id, "CONFIRMED")).Error(status, code);
            (await s.SendTo(actor, HttpMethod.Get, AdminOrders)).Error(status, code);
            (await s.SendTo(actor, HttpMethod.Get, AdminOrders + "/" + id)).Error(status, code);
        }
        (await Change(s, admin, id, "SHIPPING")).Error(409, "INVALID_STATUS_TRANSITION");
        (await Change(s, admin, id, "Refunded")).Error(400, "VALIDATION_ERROR");
        (await Change(s, admin, id, "CANCELLED", "bad")).Error(400, "VALIDATION_ERROR");
        Success(await Change(s, admin, id, "CONFIRMED"), "CONFIRMED");
        (await Cancel(s, buyer, id)).Error(409, "INVALID_STATUS_TRANSITION");
        var shipping = Success(await Change(s, admin, id, "SHIPPING"), "SHIPPING");
        Assert.Equal("Pending", shipping.GetProperty("payment").GetProperty("paymentStatus").GetString());
        (await Change(s, admin, id, "CANCELLED")).Error(409, "INVALID_STATUS_TRANSITION");
        var start = await s.Db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync();
        var delivered = Success(await Change(s, admin, id, "DELIVERED"), "DELIVERED");
        var end = await s.Db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync();
        var pay = delivered.GetProperty("payment");
        Assert.Equal("Paid", pay.GetProperty("paymentStatus").GetString());
        Assert.Equal(delivered.GetProperty("totalAmount").GetDecimal(), pay.GetProperty("receivedAmount").GetDecimal());
        Assert.InRange(pay.GetProperty("paidAt").GetDateTime(), start, end);
        Assert.InRange((await s.Db.Orders.AsNoTracking().SingleAsync()).DeliveredAt!.Value, start, end);
        Assert.Equal(JsonValueKind.Null, pay.GetProperty("transactionCode").ValueKind);
        Assert.Equal(4, delivered.GetProperty("statusHistory").GetArrayLength());
        (await Cancel(s, buyer, id)).Error(409, "INVALID_STATUS_TRANSITION");
        (await Change(s, admin, id, "CANCELLED")).Error(409, "INVALID_STATUS_TRANSITION");
        Assert.False(await s.Db.InventoryTransactions.AnyAsync(t => t.TransactionType == "OrderRestock"));
    });

    [SqlDevelopmentFact]
    public Task TransferCancellationAndShippingFollowActualPayment() => Run(async s =>
    {
        EnableTestBank(s);
        var a = await s.Account("buyer");
        var admin = await s.Account("admin", "Admin");
        var v = await s.Variant("shoe", 50, 8);
        foreach (var scenario in new[] { "within", "expired", "refund", "deliver" })
        {
            var id = await Buy(s, a, v, 2);
            if (scenario != "within") Success(await Change(s, admin, id, "CONFIRMED"), "CONFIRMED");
            var p = await s.Db.Payments.SingleAsync(x => x.OrderId == id);
            if (scenario == "expired")
            {
                p.CreatedAt = p.CreatedAt.AddDays(-2);
                p.ExpiresAt = p.CreatedAt.AddDays(1);
                await s.Db.SaveChangesAsync();
            }
            if (scenario is "refund" or "deliver")
            {
                (await Change(s, admin, id, "SHIPPING")).Error(409, "INVALID_STATUS_TRANSITION");
                Assert.Equal(2, await s.Db.OrderStatusHistories.CountAsync(h => h.OrderId == id));
                p.PaymentStatus = "Paid";
                p.PaidAt = p.CreatedAt;
                p.ReceivedAmount = p.Amount;
                p.TransactionCode = "TEST-" + id;
                await s.Db.SaveChangesAsync();
            }
            if (scenario == "deliver")
            {
                Success(await Change(s, admin, id, "SHIPPING"), "SHIPPING");
                var data = Success(await Change(s, admin, id, "DELIVERED"), "DELIVERED");
                Assert.Equal("Paid", data.GetProperty("payment").GetProperty("paymentStatus").GetString());
                Assert.Equal(p.PaidAt, data.GetProperty("payment").GetProperty("paidAt").GetDateTime());
                continue;
            }
            var result = await Change(s, admin, id, "CANCELLED");
            var payment = Success(result, "CANCELLED").GetProperty("payment");
            var expected = scenario switch { "refund" => "RefundPending", "expired" => "Expired", _ => "Failed" };
            Assert.Equal(expected, payment.GetProperty("paymentStatus").GetString());
            if (scenario == "refund")
            {
                Assert.Contains("chờ hoàn tiền", result.Body.GetProperty("message").GetString());
                Assert.Equal(p.TransactionCode, payment.GetProperty("transactionCode").GetString());
                Assert.Equal(p.Amount, payment.GetProperty("receivedAmount").GetDecimal());
                Assert.Equal(JsonValueKind.Null, payment.GetProperty("refundedAt").ValueKind);
                var refunds = await s.SendTo(admin, HttpMethod.Get, AdminOrders + "?paymentStatus=RefundPending");
                Assert.Equal(id, Assert.Single(refunds.Data.GetProperty("items").EnumerateArray()).GetProperty("orderId").GetInt32());
            }
        }
        Assert.Equal(3, await s.Db.InventoryTransactions.CountAsync(t => t.TransactionType == "OrderRestock"));
    });

    [SqlDevelopmentFact]
    public Task LostConditionalUpdatesRetryAtMostThreeTimes() => Run(async s =>
    {
        var buyer = await s.Account("buyer");
        var id = await Buy(s, buyer, await s.Variant("sku", 20, 2));
        s.Gate.ConflictsToSimulate = 5;
        (await Cancel(s, buyer, id)).Error(409, "CONCURRENCY_CONFLICT");
        Assert.Equal(1, s.Gate.ConflictsToSimulate);
        Assert.Equal(1, await s.Db.OrderStatusHistories.CountAsync());
        Assert.False(await s.Db.InventoryTransactions.AnyAsync(t => t.TransactionType == "OrderRestock"));
        s.Gate.ConflictsToSimulate = 3;
        Success(await Cancel(s, buyer, id), "CANCELLED");
        Assert.Equal(0, s.Gate.ConflictsToSimulate);
        Assert.Equal(1, await s.Db.InventoryTransactions.CountAsync(t => t.TransactionType == "OrderRestock"));
    });

    [SqlDevelopmentFact]
    public Task ConcurrentCancellationAndConfirmationRespectCurrentState() => Run(async s =>
    {
        var a = await s.Account("buyer");
        var admin = await s.Account("admin", "Admin");
        var v = await s.Variant("sku", 20, 5);
        foreach (var opponent in new[] { "CANCELLED", "CONFIRMED" })
        {
            var id = await Buy(s, a, v);
            s.Gate.Arm("UPDATE dbo.Orders SET StatusId", 2);
            var both = Task.WhenAll(Cancel(s, a, id), Change(s, admin, id, opponent));
            try { await s.Gate.Wait(); } finally { s.Gate.Release(); }
            var results = await both;
            var winner = Assert.Single(results, r => r.Status == 200);
            Assert.Single(results, r => r.Status == 409).Error(409, "INVALID_STATUS_TRANSITION");
            var cancelled = winner.Data.GetProperty("status").GetProperty("statusCode").GetString() == "CANCELLED";
            if (opponent == "CANCELLED") Assert.True(cancelled);
            Assert.Equal(cancelled ? 1 : 0, await s.Db.InventoryTransactions.CountAsync(t =>
                t.TransactionType == "OrderRestock" && t.OrderItem!.OrderId == id));
            Assert.Equal(cancelled ? "Failed" : "Pending", await s.Db.Payments.Where(p => p.OrderId == id).Select(p => p.PaymentStatus).SingleAsync());
            Assert.Equal(2, await s.Db.OrderStatusHistories.CountAsync(h => h.OrderId == id));
        }
    });

    [SqlDevelopmentFact]
    public Task FailedPaymentOrInventoryRestoresWholeTransaction() => Run(async s =>
    {
        var buyer = await s.Account("buyer");
        var v = await s.Variant("first", 50, 2);
        var last = await s.Variant("last", 30, 3);
        await s.Fill(buyer, v);
        var id = await Buy(s, buyer, last);
        var original = await s.Db.Orders.AsNoTracking().SingleAsync();
        foreach (var breakPayment in new[] { true, false })
        {
            s.Gate.FailPaymentUpdate = breakPayment;
            if (!breakPayment)
                await s.Db.Inventories.Where(i => i.VariantId == last.VariantId)
                    .ExecuteUpdateAsync(u => u.SetProperty(i => i.QuantityOnHand, int.MaxValue));
            (await Cancel(s, buyer, id)).Error(500, "INTERNAL_ERROR");
            var order = await s.Db.Orders.AsNoTracking().SingleAsync();
            Assert.Equal(original.RowVersion, order.RowVersion);
            Assert.Null(order.CancelReason);
            Assert.Equal("Pending", await s.Db.Payments.Select(p => p.PaymentStatus).SingleAsync());
            Assert.Equal(1, await s.Db.OrderStatusHistories.CountAsync());
            Assert.False(await s.Db.InventoryTransactions.AnyAsync(t => t.TransactionType == "OrderRestock"));
            Assert.Equal(1, await s.Db.Inventories.Where(i => i.VariantId == v.VariantId).Select(i => i.QuantityOnHand).SingleAsync());
        }
    });

    [SqlDevelopmentFact]
    public Task AdminOrderListFiltersVietnamDatesAndLatestPayment() => Run(async s =>
    {
        var buyer = await s.Account("buyer");
        var admin = await s.Account("admin", "Admin");
        var v = await s.Variant("sku", 10, 4);
        var first = await Buy(s, buyer, v, quantity: 2);
        var second = await Buy(s, buyer, v);
        await Cancel(s, buyer, second);
        var boundary = new DateTime(2020, 1, 1, 17, 0, 0, DateTimeKind.Utc);
        await s.Db.Orders.Where(o => o.OrderId == first).ExecuteUpdateAsync(u => u.SetProperty(o => o.CreatedAt, boundary.AddTicks(-1)));
        await s.Db.Orders.Where(o => o.OrderId == second).ExecuteUpdateAsync(u => u.SetProperty(o => o.CreatedAt, boundary));
        var response = await s.SendTo(admin, HttpMethod.Get, AdminOrders + "?from=2020-01-02&to=2020-01-02&statusCode=CANCELLED&paymentStatus=Failed&keyword=09123");
        Assert.Equal(200, response.Status);
        var item = Assert.Single(response.Data.GetProperty("items").EnumerateArray());
        Assert.Equal(second, item.GetProperty("orderId").GetInt32());
        Assert.Equal("COD", item.GetProperty("paymentMethodCode").GetString());
        Assert.True(item.GetProperty("customer").GetProperty("userId").GetInt32() > 0);
        Assert.Equal(20, response.Data.GetProperty("pagination").GetProperty("pageSize").GetInt32());
        var page = (await s.SendTo(admin, HttpMethod.Get, AdminOrders + "?page=1&pageSize=1&sort=createdAt")).Data;
        Assert.Equal(2, page.GetProperty("pagination").GetProperty("totalItems").GetInt32());
        Assert.Equal(2, page.GetProperty("items")[0].GetProperty("totalItems").GetInt32());
        var detail = Success(await s.SendTo(admin, HttpMethod.Get, AdminOrders + "/" + first), "PENDING");
        Assert.Equal(2, detail.GetProperty("items")[0].GetProperty("quantity").GetInt32());
        Assert.True(detail.GetProperty("payment").TryGetProperty("transactionCode", out _));
        (await s.SendTo(admin, HttpMethod.Get, AdminOrders + "/0")).Error(404, "NOT_FOUND");
        (await s.SendTo(admin, HttpMethod.Get, AdminOrders + "?from=2020-02-01&to=2020-01-01")).Error(400, "VALIDATION_ERROR");
        (await s.SendTo(admin, HttpMethod.Get, AdminOrders + "?paymentStatus=bogus")).Error(400, "VALIDATION_ERROR");
    });
}
