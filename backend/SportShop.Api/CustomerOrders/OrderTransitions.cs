using System.ComponentModel.DataAnnotations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;
using SportShop.Api.Middleware;

namespace SportShop.Api.CustomerOrders;

public sealed record CancelOrderRequest(string? CancelReason);

public sealed class ChangeStatusRequest
{
    [Required, RegularExpression("PENDING|CONFIRMED|SHIPPING|DELIVERED|CANCELLED")]
    public string StatusCode { get; init; } = "";
    public string? CancelReason { get; init; }
    [MaxLength(255)] public string? Note { get; init; }
}

public sealed class OrderTransitions(SportShopDbContext db, OrderReader reader)
{
    private const string ExpiryReason = "Hết hạn thanh toán chuyển khoản";
    private sealed record State(int StatusId, string StatusCode, byte[] Version, decimal TotalAmount);
    private sealed record CurrentPayment(int Id, string Method, string Status, decimal Amount);
    private static ApiFailure Invalid(string message = "Không thể chuyển trạng thái đơn hàng.") =>
        new(409, "INVALID_STATUS_TRANSITION", message);

    internal Task<object?> CustomerCancel(int userId, int orderId, CancelOrderRequest request, CancellationToken ct) =>
        Change(userId, userId, orderId, "CANCELLED", request.CancelReason, null, ct);
    internal Task<object?> AdminChange(int adminId, int orderId, ChangeStatusRequest request, CancellationToken ct) =>
        Change(adminId, null, orderId, request.StatusCode, request.CancelReason, request.Note, ct);

    internal Task ExpireTransfer(int orderId, int paymentId, CancellationToken ct) =>
        Change(null, null, orderId, "CANCELLED", ExpiryReason, null, ct, paymentId);

    private async Task<object?> Change(int? actor, int? owner, int orderId, string target, string? reason, string? note,
        CancellationToken ct, int? expiredPaymentId = null)
    {
        reason = reason?.Trim();
        if (target == "CANCELLED" && (reason?.Length ?? 0) is < 5 or > 255)
            throw new ApiFailure(400, "VALIDATION_ERROR", "Lý do hủy phải từ 5 đến 255 ký tự.", "cancelReason");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var state = await ReadAllowed(owner, orderId, target, ct, skipInvalid: expiredPaymentId.HasValue);
                if (state is null) return null;
                var next = await db.OrderStatuses.Where(s => s.StatusCode == target).Select(s => s.StatusId).SingleAsync(ct);
                // Acquire the order before reading its payment or touching any inventory.
                var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    DECLARE @now datetime2 = SYSUTCDATETIME();
                    UPDATE dbo.Orders SET StatusId={next}, UpdatedAt=@now,
                        DeliveredAt=CASE WHEN {target}='DELIVERED' THEN @now ELSE DeliveredAt END,
                        CancelReason=CASE WHEN {target}='CANCELLED' THEN {reason} ELSE CancelReason END
                    WHERE OrderId={orderId} AND RowVersion={state.Version} AND StatusId={state.StatusId}
                        AND ({owner} IS NULL OR UserId={owner})
                    """, ct);
                if (changed != 1)
                {
                    if (expiredPaymentId.HasValue) return null;
                    throw new DbUpdateConcurrencyException();
                }
                var refundPending = false;
                if (target != "CONFIRMED")
                {
                    var payment = await db.Payments.Where(p => p.OrderId == orderId)
                        .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.PaymentId)
                        .Select(p => new CurrentPayment(p.PaymentId, p.PaymentMethod.MethodCode, p.PaymentStatus, p.Amount))
                        .FirstOrDefaultAsync(ct);
                    if (target == "CANCELLED")
                    {
                        if (expiredPaymentId is int paymentId)
                        {
                            if (payment?.Id != paymentId || !await ExpirePayment(orderId, paymentId, ct)) return null;
                        }
                        else
                        {
                            await CancelPayment(payment ?? throw Invalid(), ct);
                            refundPending = payment.Status == "Paid";
                        }
                        await Restock(orderId, actor, reason!, ct);
                    }
                    else if (payment is null)
                        throw Invalid();
                    else if (payment.Method == "BANK_TRANSFER" && payment.Status != "Paid")
                        throw Invalid("Đơn chuyển khoản phải được thanh toán trước khi giao hàng.");
                    else if (target == "DELIVERED" && payment.Method == "COD")
                        await CollectCod(payment, state.TotalAmount, ct);
                }
                var time = await db.Orders.Where(o => o.OrderId == orderId).Select(o => o.UpdatedAt).SingleAsync(ct);
                db.OrderStatusHistories.Add(new OrderStatusHistory { OrderId = orderId, FromStatusId = state.StatusId,
                    ToStatusId = next, ChangedBy = actor, ChangedAt = time, Note = target == "CANCELLED" ? reason : note?.Trim() });
                await db.SaveChangesAsync(ct);
                if (expiredPaymentId.HasValue)
                {
                    await tx.CommitAsync(ct);
                    return null;
                }
                var data = await reader.Detail(owner, orderId, ct);
                await tx.CommitAsync(ct);
                return new { success = true, message = refundPending ? "Đã hủy đơn. Khoản thanh toán đang chờ hoàn tiền." : "OK", data };
            }
            catch (Exception ex) when (!expiredPaymentId.HasValue &&
                (ex is DbUpdateConcurrencyException || ex.GetBaseException() is SqlException { Number: 1205 }))
            {
                db.ChangeTracker.Clear();
                if (attempt >= 3)
                {
                    await ReadAllowed(owner, orderId, target, ct);
                    throw new ApiFailure(409, "CONCURRENCY_CONFLICT", "Dữ liệu vừa thay đổi, vui lòng thử lại.");
                }
            }
        }
    }

    private async Task<State?> ReadAllowed(int? owner, int orderId, string target, CancellationToken ct, bool skipInvalid = false)
    {
        var state = await db.Orders.AsNoTracking().Where(o => o.OrderId == orderId && (owner == null || o.UserId == owner))
            .Select(o => new State(o.StatusId, o.Status.StatusCode, o.RowVersion, o.TotalAmount)).SingleOrDefaultAsync(ct);
        if (state is null)
        {
            if (skipInvalid) return null;
            throw new ApiFailure(404, "NOT_FOUND", "Không tìm thấy đơn hàng.");
        }
        var allowed = target switch {
            "CONFIRMED" => state.StatusCode == "PENDING",
            "SHIPPING" => state.StatusCode == "CONFIRMED",
            "DELIVERED" => state.StatusCode == "SHIPPING",
            "CANCELLED" => state.StatusCode == "PENDING" || (owner == null && state.StatusCode == "CONFIRMED"),
            _ => false
        };
        if (!allowed)
        {
            if (skipInvalid) return null;
            throw Invalid();
        }
        return state;
    }

    private async Task<bool> ExpirePayment(int orderId, int paymentId, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @now datetime2=SYSUTCDATETIME();
            UPDATE dbo.Payments SET PaymentStatus='Expired', UpdatedAt=@now
            WHERE OrderId={orderId} AND PaymentId={paymentId} AND PaymentStatus='Pending' AND ExpiresAt<=@now
              AND PaymentMethodId IN (SELECT PaymentMethodId FROM dbo.PaymentMethods WHERE MethodCode='BANK_TRANSFER')
            """, ct) == 1;

    private async Task CancelPayment(CurrentPayment payment, CancellationToken ct)
    {
        if (payment.Status is not ("Pending" or "Paid")) throw Invalid();
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @now datetime2=SYSUTCDATETIME();
            UPDATE dbo.Payments SET PaymentStatus=CASE
                WHEN PaymentStatus='Paid' THEN 'RefundPending'
                WHEN ExpiresAt<=@now THEN 'Expired' ELSE 'Failed' END, UpdatedAt=@now
            WHERE PaymentId={payment.Id} AND PaymentStatus={payment.Status}
            """, ct);
        if (affected != 1) throw new DbUpdateConcurrencyException();
    }

    private async Task CollectCod(CurrentPayment payment, decimal total, CancellationToken ct)
    {
        if (payment.Status != "Pending" || payment.Amount != total) throw Invalid();
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @now datetime2=SYSUTCDATETIME();
            UPDATE dbo.Payments SET PaymentStatus='Paid', ReceivedAmount=Amount, PaidAt=@now, UpdatedAt=@now
            WHERE PaymentId={payment.Id} AND PaymentStatus='Pending' AND Amount={total}
            """, ct);
        if (affected != 1) throw new DbUpdateConcurrencyException();
    }

    private async Task Restock(int orderId, int? actor, string reason, CancellationToken ct)
    {
        var lines = await db.OrderItems.Where(i => i.OrderId == orderId).OrderBy(i => i.VariantId)
            .Select(i => new { i.OrderItemId, i.VariantId, i.Quantity }).ToListAsync(ct);
        foreach (var line in lines)
        {
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE dbo.Inventories SET QuantityOnHand=QuantityOnHand+{line.Quantity}, UpdatedAt=SYSUTCDATETIME()
                WHERE VariantId={line.VariantId}
                """, ct);
            if (affected != 1) throw new InvalidOperationException("Inventory missing during order restock.");
            var balance = await db.Inventories.Where(i => i.VariantId == line.VariantId)
                .Select(i => new { i.QuantityOnHand, i.UpdatedAt }).SingleAsync(ct);
            db.InventoryTransactions.Add(new InventoryTransaction { OrderItemId = line.OrderItemId, VariantId = line.VariantId,
                TransactionType = "OrderRestock", QuantityChange = line.Quantity, QuantityAfter = balance.QuantityOnHand,
                CreatedBy = actor, CreatedAt = balance.UpdatedAt, Note = reason });
        }
    }
}
