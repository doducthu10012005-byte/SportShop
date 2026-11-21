using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;

namespace SportShop.Api.Admin;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class ProductInput
{
    [Required, MaxLength(200)] public string ProductName { get; set; } = "";
    [Range(1, int.MaxValue)] public int CategoryId { get; set; }
    [Range(1, int.MaxValue)] public int BrandId { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}
public sealed class NewProduct : ProductInput
{
    [Required, MinLength(1)] public List<NewVariant> Variants { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class VariantInput
{
    [Required, MaxLength(50), RegularExpression("^[a-zA-Z0-9-]+$")] public string Sku { get; set; } = "";
    [JsonRequired, Range(typeof(decimal), "0", "1000000000")] public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class NewVariant : VariantInput
{
    [Range(1, int.MaxValue)] public int SizeId { get; set; }
    [Range(1, int.MaxValue)] public int ColorId { get; set; }
    [Range(0, int.MaxValue)] public int InitialStock { get; set; }
}

public sealed class ProductQuery : PageQuery
{
    public string? Keyword { get; set; }
    [Range(1, int.MaxValue)] public int? CategoryId { get; set; }
    [Range(1, int.MaxValue)] public int? BrandId { get; set; }
    public bool? IsActive { get; set; }
}

public sealed class ProductData(SportShopDbContext db)
{
    internal Task<object> List(ProductQuery filter, CancellationToken ct)
    {
        var rows = db.Products.AsNoTracking();
        if (filter.CategoryId.HasValue) rows = rows.Where(p => p.CategoryId == filter.CategoryId);
        if (filter.BrandId.HasValue) rows = rows.Where(p => p.BrandId == filter.BrandId);
        if (filter.IsActive.HasValue) rows = rows.Where(p => p.IsActive == filter.IsActive);
        if (!string.IsNullOrWhiteSpace(filter.Keyword))
        {
            var keyword = filter.Keyword.Trim();
            rows = rows.Where(p => p.ProductName.Contains(keyword) || p.Variants.Any(v => v.Sku.Contains(keyword)));
        }
        return filter.Read(rows, p => new
        {
            p.ProductId,
            p.ProductName,
            p.Slug,
            p.CategoryId,
            p.Category.CategoryName,
            p.BrandId,
            p.Brand.BrandName,
            p.IsActive,
            CreatedAt = DateTime.SpecifyKind(p.CreatedAt, DateTimeKind.Utc)
        },
            "ProductId", "-CreatedAt", ct, "ProductName", "CreatedAt", "IsActive");
    }

    internal async Task<object> Detail(int id, CancellationToken ct) => await db.Products.AsNoTracking()
        .Where(p => p.ProductId == id).Select(p => new
        {
            p.ProductId,
            p.ProductName,
            p.Slug,
            p.Description,
            p.IsActive,
            p.CategoryId,
            p.Category.CategoryName,
            p.BrandId,
            p.Brand.BrandName,
            CreatedAt = DateTime.SpecifyKind(p.CreatedAt, DateTimeKind.Utc),
            UpdatedAt = DateTime.SpecifyKind(p.UpdatedAt, DateTimeKind.Utc),
            variants = p.Variants.OrderBy(v => v.VariantId).Select(v => new
            {
                v.VariantId,
                v.Sku,
                v.Price,
                v.IsActive,
                v.ColorId,
                v.Color.ColorName,
                v.SizeId,
                v.Size.SizeName,
                v.Inventory!.QuantityOnHand,
                v.Inventory.LowStockThreshold
            }).ToList(),
            images = p.Images.OrderBy(i => i.ColorId).ThenBy(i => i.SortOrder).Select(i => new
            {
                i.ImageId,
                i.ImageUrl,
                i.ColorId,
                i.SortOrder,
                i.IsPrimary
            }).ToList()
        }).AsSplitQuery().SingleOrDefaultAsync(ct) ?? throw Catalog.Missing();

    internal Task<object> Save(int? id, ProductInput input, int actor, CancellationToken ct) =>
        Catalog.Write(db, async () =>
        {
            var now = DateTime.UtcNow;
            var product = id.HasValue ? await db.Products.FindAsync([id.Value], ct) ?? throw Catalog.Missing()
                : db.Products.Add(new Product { CreatedAt = now }).Entity;
            var name = input.ProductName.Trim();
            if (await db.Products.AnyAsync(p => p.ProductName.Trim() == name && p.ProductId != id, ct))
                throw Catalog.Duplicate();
            if (!await db.Categories.AnyAsync(c => c.CategoryId == input.CategoryId && c.IsActive, ct))
                throw Catalog.Invalid("categoryId");
            if (!await db.Brands.AnyAsync(b => b.BrandId == input.BrandId && b.IsActive, ct))
                throw Catalog.Invalid("brandId");
            if (!id.HasValue || product.ProductName != name)
                product.Slug = await Catalog.Slug(name, db.Products.Where(p => p.ProductId != id).Select(p => p.Slug), ct);
            product.ProductName = name;
            product.CategoryId = input.CategoryId;
            product.BrandId = input.BrandId;
            product.Description = input.Description;
            product.IsActive = input.IsActive;
            product.UpdatedAt = now;
            if (input is NewProduct create)
                foreach (var variant in create.Variants)
                    await AddVariant(product, variant ?? throw Catalog.Invalid("variants"), actor, ct);
            await db.SaveChangesAsync(ct);
            return await Detail(product.ProductId, ct);
        }, ct);

    internal Task Hide(int id, CancellationToken ct) => Catalog.Write(db, async () =>
    {
        var product = await db.Products.FindAsync([id], ct) ?? throw Catalog.Missing();
        product.IsActive = false;
        product.UpdatedAt = DateTime.UtcNow;
        return true;
    }, ct);

    private async Task CheckOptions(int size, int color, CancellationToken ct, string? field = null)
    {
        if (!await db.Sizes.AnyAsync(s => s.SizeId == size && s.IsActive, ct)) throw Catalog.Invalid(field ?? "sizeId");
        if (!await db.Colors.AnyAsync(c => c.ColorId == color && c.IsActive, ct)) throw Catalog.Invalid(field ?? "colorId");
    }

    private async Task<ProductVariant> AddVariant(Product product, NewVariant input, int actor, CancellationToken ct)
    {
        await CheckOptions(input.SizeId, input.ColorId, ct);
        if (decimal.Round(input.Price, 2) != input.Price) throw Catalog.Invalid("price");
        var now = DateTime.UtcNow;
        var variant = new ProductVariant
        {
            Product = product,
            SizeId = input.SizeId,
            ColorId = input.ColorId,
            Price = input.Price,
            Sku = input.Sku,
            IsActive = input.IsActive,
            CreatedAt = now,
            UpdatedAt = now,
            Inventory = new Inventory { QuantityOnHand = input.InitialStock, CreatedAt = now, UpdatedAt = now }
        };
        if (input.InitialStock > 0)
            variant.InventoryTransactions.Add(new InventoryTransaction
            {
                TransactionType = "Import",
                QuantityChange = input.InitialStock,
                QuantityAfter = input.InitialStock,
                CreatedBy = actor,
                CreatedAt = now
            });
        db.ProductVariants.Add(variant);
        return variant;
    }

    internal Task<object> CreateVariant(int productId, NewVariant input, int actor, CancellationToken ct) =>
        Catalog.Write(db, async () =>
        {
            var product = await db.Products.FindAsync([productId], ct) ?? throw Catalog.Missing();
            var variant = await AddVariant(product, input, actor, ct);
            await db.SaveChangesAsync(ct);
            return VariantView(variant);
        }, ct);

    private static object VariantView(ProductVariant v) => new
    {
        v.VariantId,
        v.ProductId,
        v.SizeId,
        v.ColorId,
        v.Sku,
        v.Price,
        v.IsActive
    };

    internal Task<object> UpdateVariant(int productId, int variantId, VariantInput? input, CancellationToken ct) =>
        Catalog.Write(db, async () =>
        {
            var v = await db.ProductVariants.SingleOrDefaultAsync(x => x.ProductId == productId && x.VariantId == variantId, ct)
                ?? throw Catalog.Missing();
            if (input is not null)
            {
                if (input.IsActive && !v.IsActive) await CheckOptions(v.SizeId, v.ColorId, ct, "isActive");
                if (decimal.Round(input.Price, 2) != input.Price) throw Catalog.Invalid("price");
                v.Price = input.Price;
                v.Sku = input.Sku;
            }
            v.IsActive = input?.IsActive ?? false;
            v.UpdatedAt = DateTime.UtcNow;
            return VariantView(v);
        }, ct);
}
