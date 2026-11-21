using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;
using SportShop.Api.Middleware;

namespace SportShop.Api.CustomerOrders;

public class OrderListQuery
{
    [Range(1, int.MaxValue)] public int Page { get; set; } = 1;
    [Range(1, 50)] public int PageSize { get; set; } = 12;
    [RegularExpression("createdAt|-createdAt")] public string Sort { get; set; } = "-createdAt";
    [RegularExpression("PENDING|CONFIRMED|SHIPPING|DELIVERED|CANCELLED")] public string? StatusCode { get; set; }
}

public sealed class AdminOrderQuery : OrderListQuery
{
    public AdminOrderQuery() => PageSize = 20;
    public string? Keyword { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    [RegularExpression("Pending|Paid|Failed|Expired|RefundPending|Refunded")] public string? PaymentStatus { get; set; }
}

public sealed class OrderReader(SportShopDbContext db)
{
    private sealed record Buyer(int UserId, string FullName);
    private record PaymentView(string MethodCode, string MethodName, string PaymentStatus, decimal Amount,
        decimal? ReceivedAmount, DateTime? ExpiresAt, bool IsOverdue, DateTime? PaidAt, DateTime? RefundedAt);
    private sealed record AdminPayment : PaymentView
    {
        public AdminPayment(PaymentView payment, string? transactionCode) : base(payment) => TransactionCode = transactionCode;
        public string? TransactionCode { get; }
    }
    private sealed record OrderDetail(int OrderId, string OrderCode, object Status, string RecipientName, string RecipientPhone,
        string ShippingAddress, string? Note, string? CancelReason, decimal Subtotal, decimal ShippingFee, decimal TotalAmount,
        object Payment, object Items, object StatusHistory, DateTime CreatedAt,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Buyer? Customer);
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    private static DateTime Midnight(DateOnly day) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), Vietnam);
    private static DateTime Utc(DateTime time) => DateTime.SpecifyKind(time, DateTimeKind.Utc);
    private static DateTime? OptionalUtc(DateTime? time) => time.HasValue ? Utc(time.Value) : null;

    internal async Task<object> List(int? userId, OrderListQuery filter, CancellationToken ct)
    {
        var query = db.Orders.AsNoTracking();
        if (userId.HasValue) query = query.Where(o => o.UserId == userId);
        if (filter is AdminOrderQuery admin)
        {
            if (admin.From > admin.To) throw new ApiFailure(400, "VALIDATION_ERROR", "Khoảng ngày không hợp lệ.", "to");
            if (admin.From is {} from) query = query.Where(o => o.CreatedAt >= Midnight(from));
            if (admin.To is {} to && to < DateOnly.MaxValue) query = query.Where(o => o.CreatedAt < Midnight(to.AddDays(1)));
            if (!string.IsNullOrWhiteSpace(admin.Keyword))
            {
                var term = admin.Keyword.Trim();
                query = query.Where(o => o.OrderCode.Contains(term) || o.RecipientName.Contains(term) || o.RecipientPhone.Contains(term));
            }
            if (admin.PaymentStatus != null)
                query = query.Where(o => o.Payments.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.PaymentId)
                    .Select(p => p.PaymentStatus).FirstOrDefault() == admin.PaymentStatus);
        }
        if (filter.StatusCode != null) query = query.Where(o => o.Status.StatusCode == filter.StatusCode);
        var totalItems = await query.CountAsync(ct);
        var ordered = filter.Sort == "createdAt" ? query.OrderBy(o => o.CreatedAt).ThenBy(o => o.OrderId) :
            query.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.OrderId);
        var offset = (long)(filter.Page - 1) * filter.PageSize;
        var items = offset >= totalItems ? [] : await ordered.Skip((int)offset).Take(filter.PageSize)
            .Select(o => new { o.OrderId, o.OrderCode, status = new { o.Status.StatusCode, o.Status.StatusName },
                customer = userId == null ? new Buyer(o.UserId, o.User.FullName) : null, o.RecipientName,
                o.TotalAmount, totalItems = o.Items.Sum(i => (long)i.Quantity),
                latest = o.Payments.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.PaymentId)
                    .Select(p => new { p.PaymentStatus, p.PaymentMethod.MethodName, p.PaymentMethod.MethodCode }).First(),
                createdAt = Utc(o.CreatedAt) }).ToListAsync(ct);
        return new { items = items.Select(o => userId == null
            ? (object)new { o.OrderId, o.OrderCode, o.status, o.customer, o.RecipientName, o.TotalAmount,
                o.totalItems, paymentMethodCode = o.latest.MethodCode, o.latest.PaymentStatus, o.createdAt }
            : new { o.OrderId, o.OrderCode, o.status, o.TotalAmount,
                o.totalItems, o.latest.PaymentStatus, paymentMethodName = o.latest.MethodName, o.createdAt }),
            pagination = new { filter.Page, filter.PageSize, totalItems, totalPages = (int)Math.Ceiling((decimal)totalItems / filter.PageSize) } };
    }

    internal async Task<object> Detail(int? userId, int orderId, CancellationToken ct)
    {
        await using var tx = db.Database.CurrentTransaction == null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct) : null;
        var order = await db.Orders.AsNoTracking().AsSplitQuery().Where(o => o.OrderId == orderId && (userId == null || o.UserId == userId))
            .Select(o => new { o.OrderId, o.OrderCode, status = new { o.Status.StatusCode, o.Status.StatusName },
                customer = userId == null ? new Buyer(o.UserId, o.User.FullName) : null,
                o.RecipientName, o.RecipientPhone, o.ShippingAddress, o.Note, o.CancelReason, o.Subtotal, o.ShippingFee, o.TotalAmount,
                payment = o.Payments.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.PaymentId)
                    .Select(p => new { p.PaymentMethod.MethodCode, p.PaymentMethod.MethodName, p.PaymentStatus, p.Amount,
                        p.ReceivedAmount, p.ExpiresAt, p.PaidAt, p.RefundedAt, transactionCode = userId == null ? p.TransactionCode : null }).First(),
                items = o.Items.OrderBy(i => i.OrderItemId).Select(i => new { i.OrderItemId, i.VariantId,
                    i.ProductName, i.Sku, i.SizeName, i.ColorName, i.ImageUrl, i.UnitPrice, i.Quantity, i.LineTotal }).ToList(),
                statusHistory = o.StatusHistories.OrderBy(h => h.ChangedAt).ThenBy(h => h.HistoryId)
                    .Select(h => new { h.ToStatus.StatusCode, h.ToStatus.StatusName, changedAt = Utc(h.ChangedAt), h.Note }).ToList(),
                createdAt = Utc(o.CreatedAt) }).SingleOrDefaultAsync(ct) ??
            throw new ApiFailure(404, "NOT_FOUND", "Không tìm thấy đơn hàng.");
        var p = order.payment;
        var now = await db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync(ct);
        if (tx != null) await tx.CommitAsync(ct);
        var payment = new PaymentView(p.MethodCode, p.MethodName, p.PaymentStatus, p.Amount, p.ReceivedAmount,
            OptionalUtc(p.ExpiresAt), p.PaymentStatus == "Pending" && p.ExpiresAt <= now, OptionalUtc(p.PaidAt), OptionalUtc(p.RefundedAt));
        return new OrderDetail(order.OrderId, order.OrderCode, order.status, order.RecipientName, order.RecipientPhone,
            order.ShippingAddress, order.Note, order.CancelReason, order.Subtotal, order.ShippingFee, order.TotalAmount,
            userId == null ? new AdminPayment(payment, p.transactionCode) : payment,
            order.items, order.statusHistory, order.createdAt, order.customer);
    }
}
