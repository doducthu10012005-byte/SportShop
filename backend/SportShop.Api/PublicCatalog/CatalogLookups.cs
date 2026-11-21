using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;

namespace SportShop.Api.PublicCatalog;

public sealed record CategoryNode(int CategoryId, string CategoryName, string Slug)
{
    public List<CategoryNode> Children { get; } = [];
}

public sealed class CatalogLookups(SportShopDbContext db)
{
    internal async Task<List<CategoryNode>> Categories(CancellationToken ct)
    {
        var rows = await db.Categories.AsNoTracking().Where(c => c.IsActive)
            .OrderBy(c => c.CategoryName).ThenBy(c => c.CategoryId)
            .Select(c => new { c.CategoryId, c.CategoryName, c.Slug, c.ParentCategoryId }).ToListAsync(ct);
        var nodes = rows.ToDictionary(c => c.CategoryId, c => new CategoryNode(c.CategoryId, c.CategoryName, c.Slug));
        var roots = new List<CategoryNode>();
        foreach (var row in rows)
        {
            var node = nodes[row.CategoryId];
            if (row.ParentCategoryId is int parent && nodes.TryGetValue(parent, out var parentNode))
                parentNode.Children.Add(node);
            else
                roots.Add(node);
        }
        return roots;
    }

    internal Task<List<int>> CategoryBranch(int root, CancellationToken ct) =>
        db.Database.SqlQuery<int>($"""
            WITH branch AS (
                SELECT CategoryId FROM Categories WHERE CategoryId = {root} AND IsActive = 1
                UNION ALL
                SELECT c.CategoryId FROM Categories c JOIN branch b ON b.CategoryId = c.ParentCategoryId
                WHERE c.IsActive = 1
            )
            SELECT CategoryId AS [Value] FROM branch OPTION (MAXRECURSION 0)
            """).ToListAsync(ct);

    internal async Task<object> Brands(CancellationToken ct) => await db.Brands.AsNoTracking().Where(b => b.IsActive)
        .OrderBy(b => b.BrandName).ThenBy(b => b.BrandId).Select(b => new { b.BrandId, b.BrandName, b.LogoUrl }).ToListAsync(ct);

    internal async Task<object> Sizes(CancellationToken ct) => await db.Sizes.AsNoTracking().Where(s => s.IsActive)
        .OrderBy(s => s.SortOrder).ThenBy(s => s.SizeId).Select(s => new { s.SizeId, s.SizeName }).ToListAsync(ct);

    internal async Task<object> Colors(CancellationToken ct) => await db.Colors.AsNoTracking().Where(c => c.IsActive)
        .OrderBy(c => c.ColorName).ThenBy(c => c.ColorId).Select(c => new { c.ColorId, c.ColorName, c.HexCode }).ToListAsync(ct);

    internal async Task<object> PaymentMethods(CancellationToken ct) => await db.PaymentMethods.AsNoTracking().Where(m => m.IsActive)
        .OrderBy(m => m.PaymentMethodId).Select(m => new { m.PaymentMethodId, m.MethodCode, m.MethodName }).ToListAsync(ct);
}
