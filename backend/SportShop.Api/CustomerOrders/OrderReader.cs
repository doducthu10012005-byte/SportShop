using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;
using SportShop.Api.Middleware;

namespace SportShop.Api.CustomerOrders;

public sealed class OrderListQuery
{
    [Range(1, int.MaxValue)] public int Page { get; set; } = 1;
    [Range(1, 50)] public int PageSize { get; set; } = 12;
    [RegularExpression("createdAt|-createdAt")] public string Sort { get; set; } = "-createdAt";
    [RegularExpression("PENDING|CONFIRMED|SHIPPING|DELIVERED|CANCELLED")] public string? StatusCode { get; set; }
}

public sealed class OrderReader(SportShopDbContext db)
{
    private static DateTime Utc(DateTime time) => DateTime.SpecifyKind(time, DateTimeKind.Utc);
    private static DateTime? OptionalUtc(DateTime? time) => time.HasValue ? Utc(time.Value) : null;

    internal async Task<object> List(int userId, OrderListQuery filter, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking().Where(o => o.UserId == userId);
        if (filter.StatusCode != null) query = query.Where(o => o.Status.StatusCode == filter.StatusCode);
        var totalItems = await query.CountAsync(ct);
        var ordered = filter.Sort == "createdAt" ? query.OrderBy(o => o.CreatedAt).ThenBy(o => o.OrderId) :
            query.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.OrderId);
        var offset = (long)(filter.Page - 1) * filter.PageSize;
        var items = offset >= totalItems ? [] : await ordered.Skip((int)offset).Take(filter.PageSize)
            .Select(o => new { o.OrderId, o.OrderCode, status = new { o.Status.StatusCode, o.Status.StatusName },
                o.TotalAmount, totalItems = o.Items.Sum(i => (long)i.Quantity),
                latest = o.Payments.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.PaymentId)
                    .Select(p => new { p.PaymentStatus, p.PaymentMethod.MethodName }).First(),
                createdAt = Utc(o.CreatedAt) }).ToListAsync(ct);
        return new { items = items.Select(o => new { o.OrderId, o.OrderCode, o.status, o.TotalAmount,
                o.totalItems, o.latest.PaymentStatus, paymentMethodName = o.latest.MethodName, o.createdAt }),
            pagination = new { filter.Page, filter.PageSize, totalItems, totalPages = (int)Math.Ceiling((decimal)totalItems / filter.PageSize) } };
    }

    internal async Task<object> Detail(int userId, int orderId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var order = await db.Orders.AsNoTracking().AsSplitQuery().Where(o => o.OrderId == orderId && o.UserId == userId)
            .Select(o => new { o.OrderId, o.OrderCode, status = new { o.Status.StatusCode, o.Status.StatusName },
                o.RecipientName, o.RecipientPhone, o.ShippingAddress, o.Note, o.CancelReason, o.Subtotal, o.ShippingFee, o.TotalAmount,
                payment = o.Payments.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.PaymentId)
                    .Select(p => new { p.PaymentMethod.MethodCode, p.PaymentMethod.MethodName, p.PaymentStatus, p.Amount,
                        p.ReceivedAmount, p.ExpiresAt, p.PaidAt, p.RefundedAt }).First(),
                items = o.Items.OrderBy(i => i.OrderItemId).Select(i => new { i.OrderItemId, i.VariantId,
                    i.ProductName, i.Sku, i.SizeName, i.ColorName, i.ImageUrl, i.UnitPrice, i.Quantity, i.LineTotal }).ToList(),
                statusHistory = o.StatusHistories.OrderBy(h => h.ChangedAt).ThenBy(h => h.HistoryId)
                    .Select(h => new { h.ToStatus.StatusCode, h.ToStatus.StatusName, changedAt = Utc(h.ChangedAt), h.Note }).ToList(),
                createdAt = Utc(o.CreatedAt) }).SingleOrDefaultAsync(ct) ??
            throw new ApiFailure(404, "NOT_FOUND", "Không tìm thấy đơn hàng.");
        var p = order.payment;
        var now = await db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync(ct);
        await tx.CommitAsync(ct);
        return new { order.OrderId, order.OrderCode, order.status, order.RecipientName, order.RecipientPhone,
            order.ShippingAddress, order.Note, order.CancelReason, order.Subtotal, order.ShippingFee, order.TotalAmount,
            payment = new { p.MethodCode, p.MethodName, p.PaymentStatus, p.Amount, p.ReceivedAmount,
                expiresAt = OptionalUtc(p.ExpiresAt), isOverdue = p.PaymentStatus == "Pending" && p.ExpiresAt <= now,
                paidAt = OptionalUtc(p.PaidAt), refundedAt = OptionalUtc(p.RefundedAt) },
            order.items, order.statusHistory, order.createdAt };
    }
}
