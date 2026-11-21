using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;
using SportShop.Api.Middleware;

namespace SportShop.Api.PublicCatalog;

public sealed class PublicProducts(SportShopDbContext db, CatalogLookups lookups)
{
    private IQueryable<Product> Products => db.Products.AsNoTracking()
        .Where(p => p.IsActive && p.Category.IsActive && p.Brand.IsActive);

    private IQueryable<ProductVariant> Variants => db.ProductVariants.AsNoTracking()
        .Where(v => v.IsActive && v.Color.IsActive && v.Size.IsActive);

    internal async Task<object> List(CatalogQuery filter, CancellationToken ct)
    {
        var products = Products;
        if (!string.IsNullOrWhiteSpace(filter.Keyword))
        {
            var keyword = filter.Keyword.Trim();
            products = products.Where(p => p.ProductName.Contains(keyword));
        }
        if (filter.BrandId.Length > 0) products = products.Where(p => EF.Parameter(filter.BrandId).Contains(p.BrandId));
        if (filter.CategoryId is int categoryId)
        {
            var branch = await lookups.CategoryBranch(categoryId, ct);
            products = products.Where(p => EF.Parameter(branch).Contains(p.CategoryId));
        }

        // The same variant must satisfy every supplied variant filter.
        var variants = Variants;
        var matching = variants;
        if (filter.SizeId.Length > 0) matching = matching.Where(v => EF.Parameter(filter.SizeId).Contains(v.SizeId));
        if (filter.ColorId.Length > 0) matching = matching.Where(v => EF.Parameter(filter.ColorId).Contains(v.ColorId));
        if (filter.MinPrice.HasValue) matching = matching.Where(v => v.Price >= filter.MinPrice);
        if (filter.MaxPrice.HasValue) matching = matching.Where(v => v.Price <= filter.MaxPrice);
        if (filter.InStock) matching = matching.Where(v => v.Inventory != null && v.Inventory.QuantityOnHand > 0);
        products = products.Where(p => matching.Any(v => v.ProductId == p.ProductId));

        var total = await products.CountAsync(ct);
        var pagination = new { filter.Page, filter.PageSize, totalItems = total, totalPages = (int)Math.Ceiling((double)total / filter.PageSize) };
        var offset = (long)(filter.Page - 1) * filter.PageSize;
        if (offset >= total) return new { items = Array.Empty<object>(), pagination };

        var cards = products.Select(p => new
        {
            p.ProductId, p.ProductName, p.Slug, p.CreatedAt, p.Brand.BrandName, p.Category.CategoryName,
            PrimaryImageUrl = p.Images.Where(i => i.IsPrimary).Select(i => i.ImageUrl).FirstOrDefault(),
            MinPrice = variants.Where(v => v.ProductId == p.ProductId).Min(v => v.Price),
            MaxPrice = variants.Where(v => v.ProductId == p.ProductId).Max(v => v.Price),
            InStock = variants.Any(v => v.ProductId == p.ProductId && v.Inventory != null && v.Inventory.QuantityOnHand > 0)
        });
        var ordered = filter.Sort switch
        {
            "price" => cards.OrderBy(p => p.MinPrice),
            "-price" => cards.OrderByDescending(p => p.MinPrice),
            "name" => cards.OrderBy(p => p.ProductName),
            _ => cards.OrderByDescending(p => p.CreatedAt)
        };
        var page = await ordered.ThenBy(p => p.ProductId).Skip((int)offset).Take(filter.PageSize).ToListAsync(ct);
        var pageIds = page.Select(p => p.ProductId).ToArray();
        var colors = await variants.Where(v => pageIds.Contains(v.ProductId))
            .Select(v => new { v.ProductId, v.ColorId, v.Color.ColorName, v.Color.HexCode }).Distinct()
            .OrderBy(v => v.ColorName).ThenBy(v => v.ColorId).ToListAsync(ct);
        var byProduct = colors.ToLookup(c => c.ProductId, c => new { c.ColorId, c.ColorName, c.HexCode });
        var items = page.Select(p => new { p.ProductId, p.ProductName, p.Slug, p.BrandName, p.CategoryName,
            p.PrimaryImageUrl, p.MinPrice, p.MaxPrice, p.InStock, colors = byProduct[p.ProductId] });
        return new { items, pagination };
    }

    internal async Task<object> Detail(int id, CancellationToken ct)
    {
        var variants = Variants.Where(v => v.ProductId == id);
        var detail = await Products.Where(p => p.ProductId == id && variants.Any())
            .Select(p => new { p.ProductId, p.ProductName, p.Slug, p.Description,
                brand = new { p.BrandId, p.Brand.BrandName }, category = new { p.CategoryId, p.Category.CategoryName },
                images = p.Images.OrderBy(i => i.ColorId).ThenBy(i => i.SortOrder).ThenBy(i => i.ImageId)
                    .Select(i => new { i.ImageId, i.ImageUrl, i.ColorId, i.IsPrimary }).ToList(),
                variants = variants.Where(v => v.ProductId == p.ProductId).OrderBy(v => v.Size.SortOrder).ThenBy(v => v.VariantId)
                    .Select(v => new { v.VariantId, v.Sku, v.SizeId, v.Size.SizeName, v.ColorId, v.Color.ColorName,
                        v.Price, QuantityAvailable = v.Inventory == null ? 0 : v.Inventory.QuantityOnHand }).ToList()
            }).AsSplitQuery().SingleOrDefaultAsync(ct);
        return detail ?? throw new ApiFailure(404, "NOT_FOUND", "Không tìm thấy sản phẩm.");
    }
}
