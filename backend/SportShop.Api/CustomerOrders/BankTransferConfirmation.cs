using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;
using SportShop.Api.Middleware;

namespace SportShop.Api.CustomerOrders;

public sealed class ConfirmBankTransferRequest
{
    [Required, RegularExpression("Paid")] public string? PaymentStatus { get; init; }
    public string? TransactionCode { get; init; }
    public JsonElement ReceivedAmount { get; init; }
}

public sealed class BankTransferConfirmation(SportShopDbContext db, OrderReader reader, ILogger<BankTransferConfirmation> logger)
{
    private static readonly EventId TransferConfirmed = new(1, nameof(TransferConfirmed));
    private const decimal MaxMoney = 9999999999999999.99m;
    private sealed record OrderState(string Status, byte[] Version, decimal Total, string Code);
    private sealed record PaymentState(int Id, string Method, string Status, DateTime? ExpiresAt,
        decimal Amount, decimal? ReceivedAmount, bool CodeMatches);

    internal async Task<object> Confirm(int orderId, int adminId, ConfirmBankTransferRequest request, CancellationToken ct)
    {
        var code = request.TransactionCode?.Trim();
        if (code is null || code.Length is < 1 or > 100)
            throw new ApiFailure(400, "VALIDATION_ERROR", "Mã giao dịch phải từ 1 đến 100 ký tự.", "transactionCode");
        if (!TryReadAmount(request.ReceivedAmount, out var amount))
            throw new ApiFailure(400, "VALIDATION_ERROR", "Số tiền nhận không hợp lệ.", "receivedAmount");

        for (var retries = 0; ; retries++)
        {
            try
            {
                var order = await ReadOrder(orderId, code, ct);
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE dbo.Orders SET UpdatedAt=SYSUTCDATETIME()
                    WHERE OrderId={orderId} AND RowVersion={order.Version}
                      AND StatusId IN (SELECT StatusId FROM dbo.OrderStatuses WHERE StatusCode IN ('PENDING','CONFIRMED'))
                    """, ct);
                if (claimed != 1) throw new DbUpdateConcurrencyException();

                var payment = await ReadPayment(orderId, code, ct);
                await CheckPayment(payment, amount, ct);
                if (payment!.Status == "Paid")
                {
                    var existing = await reader.Detail(null, orderId, ct);
                    // Release the order claim without persisting its UpdatedAt or RowVersion.
                    await tx.RollbackAsync(ct);
                    return existing;
                }

                if (amount != order.Total || amount != payment.Amount)
                    throw new ApiFailure(409, "PAYMENT_AMOUNT_MISMATCH", "Số tiền nhận phải bằng tổng tiền đơn hàng.",
                        "receivedAmount", "Số tiền nhận không khớp.");
                var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    DECLARE @now datetime2 = SYSUTCDATETIME();
                    UPDATE dbo.Payments
                    SET PaymentStatus='Paid', ReceivedAmount={amount}, PaidAt=@now, TransactionCode={code}, UpdatedAt=@now
                    WHERE OrderId={orderId} AND PaymentId={payment.Id} AND PaymentStatus='Pending' AND ExpiresAt>@now
                    """, ct);
                if (updated != 1) throw new DbUpdateConcurrencyException();

                var paidAt = await db.Payments.Where(p => p.PaymentId == payment.Id)
                    .Select(p => p.PaidAt!.Value).SingleAsync(ct);
                var detail = await reader.Detail(null, orderId, ct);
                await tx.CommitAsync(ct);
                logger.LogInformation(TransferConfirmed,
                    "Admin {AdminId} confirmed bank transfer for order {OrderCode} ({OrderId}), payment {PaymentId} at {PaidAtUtc}. Result: {Result}.",
                    adminId, order.Code, orderId, payment.Id, DateTime.SpecifyKind(paidAt, DateTimeKind.Utc), "Success");
                return detail;
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627 &&
                ex.Message.Contains("UX_Payments_Method_TransactionCode", StringComparison.Ordinal))
            {
                throw new ApiFailure(409, "DUPLICATE_TRANSACTION", "Mã giao dịch đã được sử dụng.");
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException || ex.GetBaseException() is SqlException { Number: 1205 })
            {
                if (retries >= 3)
                {
                    await ReadOrder(orderId, code, ct);
                    await CheckPayment(await ReadPayment(orderId, code, ct), amount, ct);
                    throw new ApiFailure(409, "CONCURRENCY_CONFLICT", "Dữ liệu vừa thay đổi, vui lòng thử lại.");
                }
            }
        }
    }

    private static bool TryReadAmount(JsonElement input, out decimal amount)
    {
        amount = 0;
        if (input.ValueKind != JsonValueKind.Number || !input.TryGetDecimal(out amount) || amount < 0 || amount > MaxMoney)
            return false;

        // Decimal parsing may round a long JSON literal. Check its scale before trusting the parsed value.
        var literal = input.GetRawText().AsSpan();
        var exponentStart = literal.IndexOfAny('e', 'E');
        var significand = exponentStart < 0 ? literal : literal[..exponentStart];
        var lastDigit = significand.Length - 1;
        while (lastDigit >= 0 && significand[lastDigit] is '0' or '.' or '-') lastDigit--;
        if (lastDigit < 0) return true;
        var exponent = 0;
        if (exponentStart >= 0 && !int.TryParse(literal[(exponentStart + 1)..], NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent))
            return false;
        var dot = significand.IndexOf('.');
        var fractionalDigits = dot < 0 ? lastDigit - significand.Length + 1 :
            lastDigit > dot ? lastDigit - dot : lastDigit - dot + 1;
        return amount != 0 && (long)fractionalDigits - exponent <= 2 && decimal.Round(amount, 2) == amount;
    }

    private async Task<OrderState> ReadOrder(int orderId, string code, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Where(o => o.OrderId == orderId)
            .Select(o => new OrderState(o.Status.StatusCode, o.RowVersion, o.TotalAmount, o.OrderCode)).SingleOrDefaultAsync(ct)
            ?? throw new ApiFailure(404, "NOT_FOUND", "Không tìm thấy đơn hàng.");
        if (order.Status is "SHIPPING" or "DELIVERED") throw Invalid();
        if (order.Status == "CANCELLED")
        {
            var payment = await ReadPayment(orderId, code, ct);
            if (payment?.Method == "BANK_TRANSFER")
            {
                if (payment.Status == "Expired") throw Expired();
                if (payment.Status is "RefundPending" or "Refunded") throw AlreadyConfirmed();
            }
            throw Invalid();
        }
        if (order.Status is not ("PENDING" or "CONFIRMED")) throw Invalid();
        return order;
    }

    // The database collation compares codes the same way as the unique index.
    private Task<PaymentState?> ReadPayment(int orderId, string code, CancellationToken ct) =>
        db.Payments.AsNoTracking().Where(p => p.OrderId == orderId)
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.PaymentId)
            .Select(p => new PaymentState(p.PaymentId, p.PaymentMethod.MethodCode, p.PaymentStatus, p.ExpiresAt,
                p.Amount, p.ReceivedAmount, p.TransactionCode == code))
            .FirstOrDefaultAsync(ct);

    private async Task CheckPayment(PaymentState? payment, decimal amount, CancellationToken ct)
    {
        if (payment?.Method != "BANK_TRANSFER") throw Invalid();
        if (payment.Status == "Expired")
            throw Expired();
        if (payment.Status is "RefundPending" or "Refunded")
            throw AlreadyConfirmed();
        if (payment.Status == "Paid")
        {
            if (!payment.CodeMatches || payment.ReceivedAmount != amount) throw AlreadyConfirmed();
            return;
        }
        if (payment.Status != "Pending" || payment.ExpiresAt is null) throw Invalid();
        var now = await db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync(ct);
        if (now >= payment.ExpiresAt) throw Expired();
    }

    private static ApiFailure Invalid() =>
        new(409, "INVALID_STATUS_TRANSITION", "Không thể xác nhận thanh toán cho đơn hàng này.");
    private static ApiFailure Expired() =>
        new(409, "PAYMENT_EXPIRED", "Khoản thanh toán đã hết hạn.");
    private static ApiFailure AlreadyConfirmed() =>
        new(409, "PAYMENT_ALREADY_CONFIRMED", "Khoản thanh toán đã được xác nhận.");
}
