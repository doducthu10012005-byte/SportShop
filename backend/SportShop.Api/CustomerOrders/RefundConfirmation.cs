using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;
using SportShop.Api.Middleware;

namespace SportShop.Api.CustomerOrders;

public sealed record ConfirmRefundRequest(bool? RefundCompleted);

public sealed class RefundConfirmation(SportShopDbContext db, OrderReader reader, ILogger<RefundConfirmation> logger)
{
    private static readonly EventId RefundConfirmed = new(1, nameof(RefundConfirmed));

    internal async Task<(object Detail, string Message)> Confirm(int orderId, int adminId,
        ConfirmRefundRequest request, CancellationToken ct)
    {
        if (request.RefundCompleted != true)
            throw new ApiFailure(400, "VALIDATION_ERROR", "Phải xác nhận đã hoàn tiền.", "refundCompleted");
        var orderCode = await db.Orders.Where(o => o.OrderId == orderId).Select(o => o.OrderCode).SingleOrDefaultAsync(ct)
            ?? throw new ApiFailure(404, "NOT_FOUND", "Không tìm thấy đơn hàng.");

        for (var retries = 0; ; retries++)
        {
            try
            {
                return await ConfirmOnce(orderId, orderCode, adminId, ct);
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException ||
                ex.GetBaseException() is SqlException { Number: 1205 })
            {
                if (retries >= 3)
                    throw new ApiFailure(409, "CONCURRENCY_CONFLICT", "Dữ liệu vừa thay đổi, vui lòng thử lại.");
            }
        }
    }

    private async Task<(object Detail, string Message)> ConfirmOnce(int orderId, string orderCode, int adminId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var paymentId = await db.Payments.Where(p => p.OrderId == orderId)
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.PaymentId)
            .Select(p => (int?)p.PaymentId).FirstOrDefaultAsync(ct) ?? throw InvalidTransition();
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @now datetime2=SYSUTCDATETIME();
            UPDATE dbo.Payments SET PaymentStatus='Refunded', RefundedAt=@now, UpdatedAt=@now
            WHERE PaymentId={paymentId} AND OrderId={orderId} AND PaymentStatus='RefundPending'
            """, ct);
        var payment = await db.Payments.Where(p => p.PaymentId == paymentId)
            .Select(p => new { p.PaymentStatus, p.RefundedAt }).SingleOrDefaultAsync(ct);
        if (affected == 0 && payment?.PaymentStatus == "RefundPending") throw new DbUpdateConcurrencyException();
        if (payment?.PaymentStatus != "Refunded") throw InvalidTransition();

        var detail = await reader.Detail(null, orderId, ct);
        await tx.CommitAsync(ct);
        if (affected == 0) return (detail, "Khoản thanh toán đã được xác nhận hoàn tiền trước đó.");

        logger.LogInformation(RefundConfirmed,
            "Admin {AdminId} confirmed refund for order {OrderCode} ({OrderId}), payment {PaymentId} at {RefundedAtUtc}. Result: {Result}.",
            adminId, orderCode, orderId, paymentId, DateTime.SpecifyKind(payment.RefundedAt!.Value, DateTimeKind.Utc), "Success");
        return (detail, "Đã xác nhận hoàn tiền.");
    }

    private static ApiFailure InvalidTransition() =>
        new(409, "INVALID_STATUS_TRANSITION", "Khoản thanh toán không ở trạng thái chờ hoàn tiền.");
}
