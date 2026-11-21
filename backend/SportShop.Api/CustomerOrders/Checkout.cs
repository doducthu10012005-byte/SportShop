using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;
using SportShop.Api.Middleware;
using SportShop.Api.ShoppingCart;

namespace SportShop.Api.CustomerOrders;

public sealed class CheckoutRequest
{
    [Required, MaxLength(100)] public string RecipientName { get; init; } = "";
    [Required, RegularExpression(@"(?:0|\+84)[0-9]{9}")] public string RecipientPhone { get; init; } = "";
    [Required, MaxLength(500)] public string ShippingAddress { get; init; } = "";
    [Range(1, int.MaxValue)] public int PaymentMethodId { get; init; }
    [MaxLength(500)] public string? Note { get; init; }
}

public sealed class Checkout(SportShopDbContext db, IConfiguration config, ILogger<Checkout> logger)
{
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    private static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");
    private sealed record Line(OrderItem Item, bool Selling);
    private sealed record BankTransfer(string Bank, string Account, int Minutes);

    internal async Task<object> Place(int userId, CheckoutRequest input, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await PlaceOnce(userId, input, ct);
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException ||
                ex.GetBaseException() is SqlException { Number: 1205 or 51070 })
            {
                db.ChangeTracker.Clear();
                if (attempt >= 3)
                    throw new ApiFailure(409, "CONCURRENCY_CONFLICT", "Dữ liệu vừa thay đổi, vui lòng thử lại.");
            }
        }
    }

    private async Task<object> PlaceOnce(int userId, CheckoutRequest input, CancellationToken ct)
    {
        // Retain catalog rows and image ranges through commit so an admin edit cannot split the snapshot.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var cart = await CustomerCart.LockCart(db, userId, ct);
        var lines = cart is null ? [] : await db.CartItems.AsNoTracking().Where(i => i.CartId == cart.CartId)
            .OrderBy(i => i.VariantId)
            .Select(i => new Line(new OrderItem {
                VariantId = i.VariantId, Quantity = i.Quantity, ProductName = i.Variant.Product.ProductName,
                Sku = i.Variant.Sku, ColorName = i.Variant.Color.ColorName, SizeName = i.Variant.Size.SizeName,
                UnitPrice = i.Variant.Price, ImageUrl = i.Variant.Product.Images
                    .Where(p => p.ColorId == i.Variant.ColorId || p.IsPrimary)
                    .OrderByDescending(p => p.ColorId == i.Variant.ColorId).ThenBy(p => p.SortOrder)
                    .Select(p => p.ImageUrl).FirstOrDefault()
            }, i.Variant.IsActive && i.Variant.Product.IsActive)).ToListAsync(ct);
        if (lines.Count == 0) throw new ApiFailure(400, "CART_EMPTY", "Giỏ hàng đang trống.");
        foreach (var line in lines.Where(l => !l.Selling))
            throw new ApiFailure(400, "VALIDATION_ERROR", "Sản phẩm hoặc biến thể đã ngừng bán.", $"variantId:{line.Item.VariantId}");

        var method = await db.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(m =>
            m.PaymentMethodId == input.PaymentMethodId && m.IsActive, ct);
        if (method?.MethodCode is not ("COD" or "BANK_TRANSFER"))
            throw new ApiFailure(400, "VALIDATION_ERROR", "Phương thức thanh toán không khả dụng.", "paymentMethodId");
        var bank = method.MethodCode == "BANK_TRANSFER" ? ReadBankTransfer() : null;
        var subtotal = lines.Sum(l => l.Item.Quantity * l.Item.UnitPrice);
        var fee = ShippingFee(subtotal);
        var sales = await DeductStock(lines, userId, ct);

        var sqlTime = await db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync(ct);
        var now = DateTime.SpecifyKind(sqlTime, DateTimeKind.Utc);
        var code = await NextCode(now, ct);
        var status = await db.OrderStatuses.AsNoTracking().SingleAsync(s => s.StatusCode == "PENDING", ct);
        DateTime? expiresAt = bank is null ? null : now.AddMinutes(bank.Minutes);
        var order = new Order {
            OrderCode = code, UserId = userId, StatusId = status.StatusId, CreatedAt = now, UpdatedAt = now,
            RecipientName = input.RecipientName.Trim(), RecipientPhone = input.RecipientPhone,
            ShippingAddress = input.ShippingAddress.Trim(), Note = input.Note?.Trim(),
            Subtotal = subtotal, ShippingFee = fee, TotalAmount = subtotal + fee,
            Items = lines.Select(l => l.Item).ToList(),
            StatusHistories = [new() { ToStatusId = status.StatusId, ChangedBy = userId, ChangedAt = now }],
            Payments = [new() { PaymentMethodId = method.PaymentMethodId, PaymentStatus = "Pending",
                Amount = subtotal + fee, ExpiresAt = expiresAt, CreatedAt = now, UpdatedAt = now }]
        };
        db.Orders.Add(order);
        foreach (var sale in sales) sale.CreatedAt = now;
        db.InventoryTransactions.AddRange(sales);
        cart!.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await db.CartItems.Where(i => i.CartId == cart.CartId).ExecuteDeleteAsync(ct);
        var instructions = bank is null ? null :
            $"Chuyển khoản {order.TotalAmount.ToString("#,0.##", Vietnamese)}đ vào tài khoản {bank.Account} tại {bank.Bank}, nội dung: {code}";
        await tx.CommitAsync(ct);
        return new { order.OrderId, order.OrderCode, status = new { status.StatusCode, status.StatusName },
            order.Subtotal, order.ShippingFee, order.TotalAmount,
            payment = new { method.MethodCode, paymentStatus = "Pending", expiresAt, instructions }, order.CreatedAt };
    }

    private async Task<List<InventoryTransaction>> DeductStock(List<Line> lines, int userId, CancellationToken ct)
    {
        var sales = new List<InventoryTransaction>();
        var shortages = new List<ApiFieldError>();
        foreach (var line in lines)
        {
            var item = line.Item;
            var stock = await db.Inventories.FromSqlInterpolated(
                $"SELECT * FROM dbo.Inventories WITH (UPDLOCK, ROWLOCK) WHERE VariantId = {item.VariantId}")
                .AsNoTracking().SingleOrDefaultAsync(ct);
            if (stock is null || stock.QuantityOnHand < item.Quantity)
            {
                shortages.Add(new($"variantId:{item.VariantId}",
                    $"{item.ProductName} ({item.SizeName}, {item.ColorName}) chỉ còn {stock?.QuantityOnHand ?? 0} sản phẩm."));
                continue;
            }
            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE dbo.Inventories
                SET QuantityOnHand = QuantityOnHand - {item.Quantity}, UpdatedAt = SYSUTCDATETIME()
                WHERE InventoryId = {stock.InventoryId} AND RowVersion = {stock.RowVersion}
                  AND QuantityOnHand >= {item.Quantity}
                """, ct);
            if (affected != 1) throw new DbUpdateConcurrencyException();
            sales.Add(new() { OrderItem = item, VariantId = item.VariantId, TransactionType = "Sale",
                QuantityChange = -item.Quantity, QuantityAfter = stock.QuantityOnHand - item.Quantity, CreatedBy = userId });
        }
        if (shortages.Count > 0)
            throw new ApiFailure(409, "INSUFFICIENT_STOCK", "Một số sản phẩm không đủ số lượng.",
                fieldErrorSummary: "Một số sản phẩm không đủ số lượng.") { Errors = shortages };
        return sales;
    }

    private async Task<string> NextCode(DateTime utc, CancellationToken ct)
    {
        var date = TimeZoneInfo.ConvertTimeFromUtc(utc, Vietnam);
        var prefix = "SP" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-";
        var resource = "Orders.OrderCode." + prefix;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @rc int;
            EXEC @rc = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive',
                @LockOwner='Transaction', @LockTimeout=10000;
            IF @rc < 0 THROW 51070, 'Order code lock unavailable.', 1;
            """, ct);
        var next = await db.Database.SqlQuery<long>($"""
            SELECT COALESCE(MAX(CONVERT(bigint, SUBSTRING(OrderCode, 12, 19))), CAST(0 AS bigint)) + 1 AS [Value]
            FROM dbo.Orders WITH (READCOMMITTEDLOCK) WHERE OrderCode LIKE {prefix + "%"}
            """).SingleAsync(ct);
        return prefix + next.ToString("D4", CultureInfo.InvariantCulture);
    }

    private decimal ShippingFee(decimal subtotal)
    {
        var fee = config.GetValue<decimal?>("Shipping:Fee");
        var threshold = config.GetValue<decimal?>("Shipping:FreeThreshold");
        if (fee is null or < 0 || threshold is null or < 0 ||
            decimal.Round(fee.Value, 2) != fee || decimal.Round(threshold.Value, 2) != threshold)
            throw ConfigurationError("Shipping");
        return subtotal >= threshold.Value ? 0 : fee.Value;
    }

    private BankTransfer ReadBankTransfer()
    {
        var bank = config["Payment:BankName"]?.Trim();
        var account = config["Payment:BankAccountNumber"]?.Trim();
        if (string.IsNullOrEmpty(bank) || bank.Any(char.IsControl) || string.IsNullOrEmpty(account) ||
            !account.All(char.IsAsciiDigit) || !int.TryParse(config["Payment:BankTransferTimeoutMinutes"], out var minutes) || minutes <= 0)
            throw ConfigurationError("Payment:BankTransfer");
        return new(bank, account, minutes);
    }

    private ApiFailure ConfigurationError(string section)
    {
        logger.LogError("Checkout configuration missing or invalid in {Section}.", section);
        return new ApiFailure(500, "INTERNAL_ERROR", "Đã xảy ra lỗi hệ thống.");
    }
}
