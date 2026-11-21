using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;

namespace SportShop.Api.Admin;

public sealed class BrandInput
{
    [Required, StringLength(100)] public string BrandName { get; set; } = "";
    [StringLength(500)] public string? LogoUrl { get; set; }
    [StringLength(500)] public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class CategoryInput
{
    [Required, StringLength(100)] public string CategoryName { get; set; } = "";
    public int? ParentCategoryId { get; set; }
    [StringLength(500)] public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ColorInput
{
    [Required, StringLength(50)] public string ColorName { get; set; } = "";
    [StringLength(7, MinimumLength = 7), RegularExpression("^#[0-9A-Fa-f]{6}$")] public string? HexCode { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class SizeInput
{
    [Required, StringLength(20)] public string SizeName { get; set; } = "";
    [Range(0, int.MaxValue)] public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class LookupData(SportShopDbContext db)
{
    private async Task CheckParent(int? id, int? parent, CancellationToken ct)
    {
        var seen = new HashSet<int>();
        while (parent.HasValue)
        {
            if (parent == id || !seen.Add(parent.Value)) throw Catalog.Invalid("parentCategoryId");
            var ancestor = await db.Categories.Where(c => c.CategoryId == parent)
                .Select(c => new { c.ParentCategoryId }).SingleOrDefaultAsync(ct)
                ?? throw Catalog.Invalid("parentCategoryId");
            parent = ancestor.ParentCategoryId;
        }
    }

    private static readonly System.Linq.Expressions.Expression<Func<Brand, object>> BrandView =
        x => new { x.BrandId, x.BrandName, x.LogoUrl, x.Description, x.Slug, x.IsActive };

    internal async Task<object> ListBrands(PageQuery page, CancellationToken ct)
    {
        var rows = db.Brands.AsNoTracking()
            .Select(x => new { x.BrandId, x.BrandName, x.LogoUrl, x.Description, x.Slug, x.IsActive });
        return await page.Read(rows, "BrandId", "BrandName", ct, "BrandName", "IsActive");
    }

    private async Task EnsureBrandUnused(int id, CancellationToken ct)
    {
        if (await db.Products.AnyAsync(p => p.BrandId == id && p.IsActive, ct)) throw Catalog.InUse();
    }

    internal Task<object> SaveBrand(int? id, BrandInput input, CancellationToken ct) =>
        Catalog.Write(db, async () =>
        {
            var name = input.BrandName.Trim();
            if (await db.Brands.AnyAsync(x => x.BrandName.Trim() == name && x.BrandId != id, ct))
                throw Catalog.Duplicate();
            var now = DateTime.UtcNow;
            var row = id.HasValue
                ? await db.Brands.FindAsync([id.Value], ct) ?? throw Catalog.Missing()
                : db.Brands.Add(new Brand { CreatedAt = now }).Entity;
            if (id.HasValue && !input.IsActive) await EnsureBrandUnused(id.Value, ct);
            if (!id.HasValue || row.BrandName != name)
                row.Slug = await Catalog.Slug(name, db.Brands.Where(x => x.BrandId != id).Select(x => x.Slug), ct);
            row.BrandName = name;
            row.LogoUrl = input.LogoUrl;
            row.Description = input.Description;
            row.IsActive = input.IsActive;
            row.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return await db.Brands.Where(x => x.BrandId == row.BrandId).Select(BrandView).SingleAsync(ct);
        }, ct);

    internal Task HideBrand(int id, CancellationToken ct) => Catalog.Write(db, async () =>
    {
        var row = await db.Brands.FindAsync([id], ct) ?? throw Catalog.Missing();
        await EnsureBrandUnused(id, ct);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        return true;
    }, ct);

    private static readonly System.Linq.Expressions.Expression<Func<Category, object>> CategoryView =
        x => new { x.CategoryId, x.CategoryName, x.ParentCategoryId, x.Description, x.Slug, x.IsActive };

    internal async Task<object> ListCategories(CancellationToken ct)
    {
        var rows = db.Categories.AsNoTracking().OrderBy(x => x.CategoryName).ThenBy(x => x.CategoryId)
            .Select(x => new { x.CategoryId, x.CategoryName, x.ParentCategoryId, x.Description, x.Slug, x.IsActive });
        return await rows.ToListAsync(ct);
    }

    private async Task EnsureCategoryUnused(int id, CancellationToken ct)
    {
        if (await db.Products.AnyAsync(p => p.CategoryId == id && p.IsActive, ct) || await db.Categories.AnyAsync(c => c.ParentCategoryId == id, ct)) throw Catalog.InUse();
    }

    internal Task<object> SaveCategory(int? id, CategoryInput input, CancellationToken ct) =>
        Catalog.Write(db, async () =>
        {
            var name = input.CategoryName.Trim();
            if (await db.Categories.AnyAsync(x => x.CategoryName.Trim() == name && x.CategoryId != id, ct))
                throw Catalog.Duplicate();
            var now = DateTime.UtcNow;
            var row = id.HasValue
                ? await db.Categories.FindAsync([id.Value], ct) ?? throw Catalog.Missing()
                : db.Categories.Add(new Category { CreatedAt = now }).Entity;
            if (id.HasValue && !input.IsActive) await EnsureCategoryUnused(id.Value, ct);
            if (!id.HasValue || row.CategoryName != name)
                row.Slug = await Catalog.Slug(name, db.Categories.Where(x => x.CategoryId != id).Select(x => x.Slug), ct);
            row.CategoryName = name;
            await CheckParent(id, input.ParentCategoryId, ct);
            row.ParentCategoryId = input.ParentCategoryId;
            row.Description = input.Description;
            row.IsActive = input.IsActive;
            row.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return await db.Categories.Where(x => x.CategoryId == row.CategoryId).Select(CategoryView).SingleAsync(ct);
        }, ct);

    internal Task HideCategory(int id, CancellationToken ct) => Catalog.Write(db, async () =>
    {
        var row = await db.Categories.FindAsync([id], ct) ?? throw Catalog.Missing();
        await EnsureCategoryUnused(id, ct);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        return true;
    }, ct);

    private static readonly System.Linq.Expressions.Expression<Func<Color, object>> ColorView =
        x => new { x.ColorId, x.ColorName, x.HexCode, x.IsActive };

    internal async Task<object> ListColors(PageQuery page, CancellationToken ct)
    {
        var rows = db.Colors.AsNoTracking()
            .Select(x => new { x.ColorId, x.ColorName, x.HexCode, x.IsActive });
        return await page.Read(rows, "ColorId", "ColorName", ct, "ColorName", "IsActive");
    }

    private async Task EnsureColorUnused(int id, CancellationToken ct)
    {
        if (await db.ProductVariants.AnyAsync(v => v.ColorId == id && v.IsActive, ct)) throw Catalog.InUse();
    }

    internal Task<object> SaveColor(int? id, ColorInput input, CancellationToken ct) =>
        Catalog.Write(db, async () =>
        {
            var name = input.ColorName.Trim();
            if (await db.Colors.AnyAsync(x => x.ColorName.Trim() == name && x.ColorId != id, ct))
                throw Catalog.Duplicate();
            var now = DateTime.UtcNow;
            var row = id.HasValue
                ? await db.Colors.FindAsync([id.Value], ct) ?? throw Catalog.Missing()
                : db.Colors.Add(new Color { CreatedAt = now }).Entity;
            if (id.HasValue && !input.IsActive) await EnsureColorUnused(id.Value, ct);
            row.ColorName = name;
            row.HexCode = input.HexCode;
            row.IsActive = input.IsActive;
            row.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return await db.Colors.Where(x => x.ColorId == row.ColorId).Select(ColorView).SingleAsync(ct);
        }, ct);

    internal Task HideColor(int id, CancellationToken ct) => Catalog.Write(db, async () =>
    {
        var row = await db.Colors.FindAsync([id], ct) ?? throw Catalog.Missing();
        await EnsureColorUnused(id, ct);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        return true;
    }, ct);

    private static readonly System.Linq.Expressions.Expression<Func<Size, object>> SizeView =
        x => new { x.SizeId, x.SizeName, x.SortOrder, x.IsActive };

    internal async Task<object> ListSizes(PageQuery page, CancellationToken ct)
    {
        var rows = db.Sizes.AsNoTracking()
            .Select(x => new { x.SizeId, x.SizeName, x.SortOrder, x.IsActive });
        return await page.Read(rows, "SizeId", "SortOrder", ct, "SizeName", "SortOrder", "IsActive");
    }

    private async Task EnsureSizeUnused(int id, CancellationToken ct)
    {
        if (await db.ProductVariants.AnyAsync(v => v.SizeId == id && v.IsActive, ct)) throw Catalog.InUse();
    }

    internal Task<object> SaveSize(int? id, SizeInput input, CancellationToken ct) =>
        Catalog.Write(db, async () =>
        {
            var name = input.SizeName.Trim();
            if (await db.Sizes.AnyAsync(x => x.SizeName.Trim() == name && x.SizeId != id, ct))
                throw Catalog.Duplicate();
            var now = DateTime.UtcNow;
            var row = id.HasValue
                ? await db.Sizes.FindAsync([id.Value], ct) ?? throw Catalog.Missing()
                : db.Sizes.Add(new Size { CreatedAt = now }).Entity;
            if (id.HasValue && !input.IsActive) await EnsureSizeUnused(id.Value, ct);
            row.SizeName = name;
            row.SortOrder = input.SortOrder;
            row.IsActive = input.IsActive;
            row.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return await db.Sizes.Where(x => x.SizeId == row.SizeId).Select(SizeView).SingleAsync(ct);
        }, ct);

    internal Task HideSize(int id, CancellationToken ct) => Catalog.Write(db, async () =>
    {
        var row = await db.Sizes.FindAsync([id], ct) ?? throw Catalog.Missing();
        await EnsureSizeUnused(id, ct);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        return true;
    }, ct);
}
