using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;

namespace SportShop.Api.Admin;

public sealed class StockQuery : PageQuery
{
    public string? Keyword { get; set; }
    public bool LowStock { get; set; }
    public bool OutOfStock { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StockInput
{
    [Required] public string Type { get; set; } = "";
    [JsonRequired, Range(0, int.MaxValue)] public int Quantity { get; set; }
    [MaxLength(255)] public string? Note { get; set; }
}

public sealed class StockData(SportShopDbContext db)
{
    internal Task<object> List(StockQuery filter, CancellationToken ct)
    {
        var query = db.Inventories.AsNoTracking().Select(i => new
        {
            i.VariantId,
            i.Variant.Sku,
            i.Variant.Product.ProductName,
            i.Variant.Size.SizeName,
            i.Variant.Color.ColorName,
            i.QuantityOnHand,
            i.LowStockThreshold,
            IsLowStock = i.QuantityOnHand <= i.LowStockThreshold
        });
        if (filter.LowStock) query = query.Where(i => i.IsLowStock);
        if (filter.OutOfStock) query = query.Where(i => i.QuantityOnHand == 0);
        if (!string.IsNullOrWhiteSpace(filter.Keyword))
        {
            var keyword = filter.Keyword.Trim();
            query = query.Where(i => i.Sku.Contains(keyword) || i.ProductName.Contains(keyword));
        }
        return filter.Read(query, "VariantId", "VariantId", ct, "ProductName", "Sku", "QuantityOnHand");
    }

    internal async Task<object> History(int id, PageQuery page, CancellationToken ct)
    {
        if (!await db.Inventories.AnyAsync(i => i.VariantId == id, ct)) throw Catalog.Missing();
        var rows = db.InventoryTransactions.AsNoTracking().Where(t => t.VariantId == id);
        return await page.Read(rows, t => new
        {
            t.TransactionId,
            t.TransactionType,
            t.QuantityChange,
            t.QuantityAfter,
            t.OrderItemId,
            t.Note,
            t.CreatedBy,
            CreatedAt = DateTime.SpecifyKind(t.CreatedAt, DateTimeKind.Utc)
        },
            "TransactionId", "-TransactionId", ct, "CreatedAt");
    }

    internal static int Balance(int current, StockInput input)
    {
        if (input.Type is not ("Import" or "Adjustment")) throw Catalog.Invalid("type");
        if (input.Type == "Adjustment" && string.IsNullOrWhiteSpace(input.Note)) throw Catalog.Invalid("note");
        long next = input.Type == "Import" ? (long)current + input.Quantity : input.Quantity;
        if (input.Quantity < 0 || next > int.MaxValue || next == current) throw Catalog.Invalid("quantity");
        return (int)next;
    }

    internal async Task<object> Adjust(int id, StockInput input, int actor, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var row = await db.Inventories.SingleOrDefaultAsync(i => i.VariantId == id, ct) ?? throw Catalog.Missing();
                var balance = Balance(row.QuantityOnHand, input);
                var now = DateTime.UtcNow;
                db.InventoryTransactions.Add(new InventoryTransaction
                {
                    VariantId = id,
                    TransactionType = input.Type,
                    QuantityChange = balance - row.QuantityOnHand,
                    QuantityAfter = balance,
                    CreatedBy = actor,
                    Note = input.Note?.Trim(),
                    CreatedAt = now
                });
                row.QuantityOnHand = balance;
                row.UpdatedAt = now;
                await db.SaveChangesAsync(ct); // UPDATE includes the original RowVersion.
                await tx.CommitAsync(ct);
                return new { variantId = id, quantityOnHand = balance };
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException ||
                ex.GetBaseException() is SqlException { Number: 1205 })
            {
                if (attempt >= 3) throw Catalog.Conflict();
                db.ChangeTracker.Clear();
            }
        }
    }
}
