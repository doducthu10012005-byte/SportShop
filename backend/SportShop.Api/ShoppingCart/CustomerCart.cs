using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;
using SportShop.Api.Middleware;

namespace SportShop.Api.ShoppingCart;

public sealed class CustomerCart(SportShopDbContext db)
{
    private static ApiFailure NotFound() => new(404, "NOT_FOUND", "Không tìm thấy dữ liệu.");

    internal async Task<CartView> Read(int userId, CancellationToken ct) =>
        await db.Carts.AsNoTracking().Where(c => c.UserId == userId)
            .Select(c => new CartView(c.CartId, c.Items.OrderBy(i => i.CartItemId)
                .Select(i => new CartLine(i.CartItemId, i.VariantId, i.Variant.ProductId, i.Variant.Product.ProductName,
                    i.Variant.Sku, i.Variant.Size.SizeName, i.Variant.Color.ColorName,
                    i.Variant.Product.Images.Where(p => p.ColorId == i.Variant.ColorId || p.IsPrimary)
                        .OrderByDescending(p => p.ColorId == i.Variant.ColorId).ThenBy(p => p.SortOrder)
                        .Select(p => p.ImageUrl).FirstOrDefault(),
                    i.Variant.Price, i.Quantity, i.Variant.Inventory == null ? 0 : i.Variant.Inventory.QuantityOnHand)
                    { Selling = i.Variant.IsActive && i.Variant.Product.IsActive }).ToList()))
            .SingleOrDefaultAsync(ct) ?? throw NotFound();

    internal Task<CartView> Add(int userId, AddCartItem input, CancellationToken ct) =>
        Write(userId, async cart =>
        {
            var item = await db.CartItems.SingleOrDefaultAsync(i => i.CartId == cart.CartId && i.VariantId == input.VariantId, ct);
            var quantity = (item?.Quantity ?? 0) + input.Quantity;
            await CheckQuantity(input.VariantId, quantity, ct);
            if (item == null)
                db.CartItems.Add(new CartItem { CartId = cart.CartId, VariantId = input.VariantId,
                    Quantity = quantity, CreatedAt = cart.UpdatedAt, UpdatedAt = cart.UpdatedAt });
            else
            {
                item.Quantity = quantity;
                item.UpdatedAt = cart.UpdatedAt;
            }
        }, ct);

    internal Task<CartView> SetQuantity(int userId, int itemId, int quantity, CancellationToken ct) =>
        Write(userId, async cart =>
        {
            var item = await db.CartItems.SingleOrDefaultAsync(i => i.CartId == cart.CartId && i.CartItemId == itemId, ct)
                ?? throw NotFound();
            await CheckQuantity(item.VariantId, quantity, ct);
            item.Quantity = quantity;
            item.UpdatedAt = cart.UpdatedAt;
        }, ct);

    internal Task<CartView> Remove(int userId, int itemId, CancellationToken ct) =>
        Write(userId, async cart =>
        {
            if (await db.CartItems.Where(i => i.CartId == cart.CartId && i.CartItemId == itemId).ExecuteDeleteAsync(ct) == 0)
                throw NotFound();
        }, ct);

    internal Task<CartView> Clear(int userId, CancellationToken ct) =>
        Write(userId, cart => db.CartItems.Where(i => i.CartId == cart.CartId).ExecuteDeleteAsync(ct), ct);

    private async Task CheckQuantity(int variantId, int quantity, CancellationToken ct)
    {
        var variant = await db.ProductVariants.AsNoTracking()
            .Where(v => v.VariantId == variantId && v.IsActive && v.Product.IsActive)
            .Select(v => new { v.Product.ProductName, v.Size.SizeName, v.Color.ColorName,
                Stock = v.Inventory == null ? 0 : v.Inventory.QuantityOnHand }).SingleOrDefaultAsync(ct) ?? throw NotFound();
        if (quantity is < 1 or > 99)
            throw new ApiFailure(400, "VALIDATION_ERROR", "Số lượng của mỗi biến thể phải từ 1 đến 99.", "quantity");
        if (quantity > variant.Stock)
            throw new ApiFailure(409, "INSUFFICIENT_STOCK",
                $"{variant.ProductName} ({variant.SizeName}, {variant.ColorName}) chỉ còn {variant.Stock} sản phẩm.",
                $"variantId:{variantId}", "Một số sản phẩm không đủ số lượng.");
    }

    private async Task<CartView> Write(int userId, Func<Cart, Task> change, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                // Every cart mutation locks the same owner's row before inspecting its items.
                var cart = await db.Carts.FromSqlInterpolated(
                    $"SELECT * FROM [Carts] WITH (UPDLOCK, ROWLOCK) WHERE [UserId] = {userId}")
                    .SingleOrDefaultAsync(ct) ?? throw NotFound();
                cart.UpdatedAt = DateTime.UtcNow;
                await change(cart);
                await db.SaveChangesAsync(ct);
                var result = await Read(userId, ct);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException || ex.GetBaseException() is SqlException { Number: 1205 })
            {
                if (attempt >= 3) throw new ApiFailure(409, "CONCURRENCY_CONFLICT", "Giỏ hàng vừa thay đổi. Vui lòng thử lại.");
                db.ChangeTracker.Clear();
            }
        }
    }
}
