using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data.Entities;
using Xunit;

namespace SportShop.Api.Tests;

public sealed partial class CustomerOrderTests
{
    private const string RevenuePath = "/api/v1/admin/statistics/revenue";
    private const string ReportingWindow = "?from=2026-10-31&to=2026-11-01";

    [SqlDevelopmentFact]
    public Task RevenueValidatesTheDateWindowGroupingAndAdminRole() => Run(async shop =>
    {
        var customer = await shop.Account("buyer");
        var admin = await shop.Account("admin", "Admin");
        (await shop.SendTo(null, HttpMethod.Get, RevenuePath + ReportingWindow)).Error(401, "UNAUTHORIZED");
        (await shop.SendTo(customer, HttpMethod.Get, RevenuePath + ReportingWindow)).Error(403, "FORBIDDEN");
        foreach (var query in new[]
        {
            "?to=2026-11-01", "?from=2026-10-31", "?fromDate=2026-10-31&toDate=2026-11-01",
            "?from=2026-02-30&to=2026-11-01", "?from=10/31/2026&to=2026-11-01",
            "?from=2026-11-01&to=2026-10-31", "?from=2024-01-01&to=2025-01-01",
            ReportingWindow + "&groupBy=week", ReportingWindow + "&groupBy="
        })
            (await shop.SendTo(admin, HttpMethod.Get, RevenuePath + query)).Error(400, "VALIDATION_ERROR");

        var accepted = await shop.SendTo(admin, HttpMethod.Get, RevenuePath + "?from=2024-01-01&to=2024-12-31");
        Assert.Equal(200, accepted.Status);
        Assert.True(accepted.Body.GetProperty("success").GetBoolean());
        Assert.Equal("OK", accepted.Body.GetProperty("message").GetString());
        Assert.Equal("2024-01-01", accepted.Data.GetProperty("from").GetString());
        Assert.Equal("2024-12-31", accepted.Data.GetProperty("to").GetString());
        AssertMoney(accepted.Data.GetProperty("summary"), 0, 0, 0);
        Assert.Equal(0, accepted.Data.GetProperty("summary").GetProperty("averageSubtotal").GetDecimal());
        Assert.Empty(accepted.Data.GetProperty("series").EnumerateArray());
        Assert.All(accepted.Data.GetProperty("ordersByStatus").EnumerateArray(), s =>
            Assert.Equal(0, s.GetProperty("count").GetInt64()));
    });

    private static void AssertMoney(JsonElement value, decimal subtotal, decimal shipping, long orders, string count = "totalOrders")
    {
        Assert.Equal(subtotal, value.GetProperty("subtotal").GetDecimal());
        Assert.Equal(shipping, value.GetProperty("shippingFee").GetDecimal());
        Assert.Equal(subtotal + shipping, value.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(orders, value.GetProperty(count).GetInt64());
    }

    [SqlDevelopmentFact]
    public Task RevenueUsesInclusiveVietnamDaysAndDoesNotMultiplyOrders() => Run(async shop =>
    {
        await shop.Account("buyer");
        var admin = await shop.Account("admin", "Admin");
        var uid = await shop.Db.Users.Where(u => u.Email == "buyer@example.test").Select(u => u.UserId).SingleAsync();
        var statuses = await shop.Db.OrderStatuses.ToDictionaryAsync(s => s.StatusCode, s => s.StatusId);
        var sku = await shop.Variant("sku", 50, 2);
        var lower = new DateTime(2026, 10, 30, 17, 0, 0, DateTimeKind.Utc);
        var middle = lower.AddDays(1);
        var upper = lower.AddDays(2);
        int serial = 0;
        void Add(string status, DateTime? delivered, DateTime created, decimal subtotal, decimal fee, bool multiple = false)
        {
            var order = new Order
            {
                UserId = uid, StatusId = statuses[status], OrderCode = $"REPORT-{++serial}", RecipientName = "Private name",
                RecipientPhone = "0900000000", ShippingAddress = "Private address", CreatedAt = created, UpdatedAt = delivered ?? created,
                DeliveredAt = delivered, Subtotal = subtotal, ShippingFee = fee, TotalAmount = subtotal + fee,
                CancelReason = status == "CANCELLED" ? "Cancelled" : null
            };
            var paid = status == "DELIVERED";
            order.Payments.Add(new Payment
            {
                PaymentMethodId = 1, Amount = order.TotalAmount, PaymentStatus = paid ? "Paid" : status == "CANCELLED" ? "Failed" : "Pending",
                ReceivedAmount = paid ? order.TotalAmount : null, PaidAt = paid ? delivered : null,
                CreatedAt = created, UpdatedAt = order.UpdatedAt
            });
            if (multiple)
            {
                order.Payments.Add(new Payment { PaymentMethodId = 1, Amount = order.TotalAmount, PaymentStatus = "Failed",
                    CreatedAt = created, UpdatedAt = created });
                foreach (var price in new[] { subtotal - 10, 10 })
                    order.Items.Add(new OrderItem { VariantId = sku.VariantId, ProductName = "sku", Sku = "sku",
                        SizeName = "M", ColorName = "Đen", Quantity = 1, UnitPrice = price });
            }
            shop.Db.Orders.Add(order);
        }
        Add("DELIVERED", lower.AddTicks(-1), lower.AddDays(-2), 1000, 0);
        Add("DELIVERED", lower, lower.AddDays(-2), 100, 20, multiple: true);
        Add("DELIVERED", middle.AddTicks(-1), lower, 150, 30);
        Add("DELIVERED", middle, lower, 200, 0);
        Add("DELIVERED", upper.AddTicks(-1), lower, 250, 45);
        Add("DELIVERED", upper, lower, 1000, 0);
        foreach (var status in new[] { "PENDING", "CONFIRMED", "SHIPPING", "CANCELLED" })
            Add(status, null, middle, 900, 9);
        await shop.Db.SaveChangesAsync();

        var reply = await shop.SendTo(admin, HttpMethod.Get, RevenuePath + ReportingWindow);
        Assert.Equal(200, reply.Status);
        Assert.DoesNotContain("Private", reply.Body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("buyer@example.test", reply.Body.GetRawText(), StringComparison.Ordinal);
        var result = reply.Data;
        AssertMoney(result.GetProperty("summary"), 700, 95, 4);
        Assert.Equal(175, result.GetProperty("summary").GetProperty("averageSubtotal").GetDecimal());
        var series = result.GetProperty("series");
        Assert.Equal(2, series.GetArrayLength());
        Assert.Equal("2026-10-31", series[0].GetProperty("period").GetString());
        AssertMoney(series[0], 250, 50, 2, "orderCount");
        Assert.Equal("2026-11-01", series[1].GetProperty("period").GetString());
        AssertMoney(series[1], 450, 45, 2, "orderCount");
        var counts = result.GetProperty("ordersByStatus").EnumerateArray()
            .ToDictionary(row => row.GetProperty("statusCode").GetString()!, row => row.GetProperty("count").GetInt64());
        Assert.Equal(4, counts["DELIVERED"]); // This optional breakdown is based on CreatedAt.
        Assert.Equal(1, counts["CANCELLED"]);
        var monthly = (await shop.SendTo(admin, HttpMethod.Get, RevenuePath + ReportingWindow + "&groupBy=month")).Data;
        AssertMoney(monthly.GetProperty("summary"), 700, 95, 4);
        Assert.Equal(new[] { "2026-10", "2026-11" },
            monthly.GetProperty("series").EnumerateArray().Select(row => row.GetProperty("period").GetString()).ToArray());
        Assert.Equal(2, await shop.Db.OrderItems.CountAsync());
        Assert.False(await shop.Db.Orders.Where(o => o.Status.StatusCode == "DELIVERED").AnyAsync(o =>
            !o.Payments.Any(p => p.PaymentStatus == "Paid" && p.ReceivedAmount == o.TotalAmount)));
    });

    [SqlDevelopmentFact]
    public Task RevenueGroupsLargeOrderSetsInSqlServer() => Run(async shop =>
    {
        await shop.Account("buyer");
        var admin = await shop.Account("admin", "Admin");
        var uid = await shop.Db.Users.Where(u => u.Email == "buyer@example.test").Select(u => u.UserId).SingleAsync();
        var status = await shop.Db.OrderStatuses.Where(s => s.StatusCode == "DELIVERED").Select(s => s.StatusId).SingleAsync();
        const int count = 2500;
        await shop.Db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @orders TABLE (OrderId int NOT NULL);
            INSERT dbo.Orders (UserId, StatusId, OrderCode, RecipientName, RecipientPhone, ShippingAddress,
                Subtotal, ShippingFee, TotalAmount, CreatedAt, UpdatedAt, DeliveredAt)
            OUTPUT INSERTED.OrderId INTO @orders
            SELECT TOP ({count}) {uid}, {status}, CONCAT('LOAD-', ROW_NUMBER() OVER (ORDER BY a.object_id,b.object_id)),
                N'Test', N'0900000000', N'Test', 12.50, 2, 14.50, '2026-11-01T00:00:00', '2026-11-01T00:00:00', '2026-11-01T00:00:00'
            FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b;
            INSERT dbo.Payments (OrderId, PaymentMethodId, Amount, ReceivedAmount, PaymentStatus, CreatedAt, UpdatedAt, PaidAt)
            SELECT OrderId, 1, 14.50, 14.50, 'Paid', '2026-11-01T00:00:00', '2026-11-01T00:00:00', '2026-11-01T00:00:00' FROM @orders;
            """);
        // The gate verifies that the API actually executes the aggregate SQL against the order set.
        shop.Gate.Arm("GROUP BY bucket.Period", 1);
        var request = shop.SendTo(admin, HttpMethod.Get, RevenuePath + ReportingWindow + "&groupBy=day");
        try { await shop.Gate.Wait(); } finally { shop.Gate.Release(); }
        var reply = await request;
        Assert.Equal(200, reply.Status);
        AssertMoney(reply.Data.GetProperty("summary"), count * 12.50m, count * 2, count);
        var bucket = Assert.Single(reply.Data.GetProperty("series").EnumerateArray());
        Assert.Equal("2026-11-01", bucket.GetProperty("period").GetString());
        AssertMoney(bucket, count * 12.50m, count * 2, count, "orderCount");
    });
}
