using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;
using SportShop.Api.Middleware;

namespace SportShop.Api.Admin;

[ApiController, Authorize(Policy = "Admin")]
public abstract class AdminController : ControllerBase
{
    protected int ActorId => int.Parse(User.FindFirst("sub")!.Value, CultureInfo.InvariantCulture);
    protected IActionResult Success(object? data = null, int status = 200, string message = "Thành công.") =>
        StatusCode(status, new { success = true, message, data });
}

public class PageQuery
{
    [Range(1, int.MaxValue)] public int Page { get; set; } = 1;
    [Range(1, 50)] public int PageSize { get; set; } = 20;
    public string? Sort { get; set; }

    internal Task<object> Read<T>(IQueryable<T> query, string id, string defaultSort,
        CancellationToken ct, params string[] allowed) where T : class =>
        Read(query, row => row, id, defaultSort, ct, allowed);

    internal async Task<object> Read<T, TResult>(IQueryable<T> query, Expression<Func<T, TResult>> project, string id, string defaultSort,
        CancellationToken ct, params string[] allowed) where T : class
    {
        var sort = Sort ?? defaultSort;
        var descending = sort.StartsWith('-');
        var key = descending ? sort[1..] : sort;
        var field = allowed.Append(id).FirstOrDefault(s => string.Equals(s, key, StringComparison.OrdinalIgnoreCase))
            ?? throw Catalog.Invalid("sort");
        static Expression<Func<T, object>> Property(string name)
        {
            var row = Expression.Parameter(typeof(T));
            return Expression.Lambda<Func<T, object>>(Expression.Convert(Expression.Property(row, name), typeof(object)), row);
        }
        var ordered = descending ? query.OrderByDescending(Property(field)) : query.OrderBy(Property(field));
        var total = await query.CountAsync(ct);
        var offset = (long)(Page - 1) * PageSize;
        var items = offset >= total ? [] : await ordered.ThenBy(Property(id))
            .Skip((int)offset).Take(PageSize).Select(project).ToListAsync(ct);
        return new { items, pagination = new { Page, PageSize, totalItems = total, totalPages = (int)Math.Ceiling((double)total / PageSize) } };
    }
}

internal static class Catalog
{
    public static ApiFailure Invalid(string field) => new(400, "VALIDATION_ERROR", "Giá trị không hợp lệ.", field);
    public static ApiFailure Missing() => new(404, "NOT_FOUND", "Không tìm thấy dữ liệu.");
    public static ApiFailure Duplicate() => new(409, "DUPLICATE_NAME", "Tên đã được sử dụng.");
    public static ApiFailure InUse() => new(409, "IN_USE", "Dữ liệu đang được sử dụng.");
    public static ApiFailure Conflict() => new(409, "CONCURRENCY_CONFLICT", "Dữ liệu vừa thay đổi. Vui lòng thử lại.");

    // One transaction-owned lock for catalog writers. In particular, no per-name lock
    // derived from .NET string equality: SQL collation decides which names are equal.
    public static async Task<T> Write<T>(SportShopDbContext db, Func<Task<T>> action, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                DECLARE @rc int;
                EXEC @rc = sys.sp_getapplock @Resource=N'SportShop.AdminCatalog.Write',
                    @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000;
                IF @rc < 0 THROW 50002, 'Catalog lock unavailable', 1;
                """, ct);
            var result = await action();
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (SqlException ex) when (ex.Number is 50002 or 1205) { throw Conflict(); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 } sql)
        {
            if (sql.Message.Contains("UQ_ProductVariants_Sku", StringComparison.Ordinal))
                throw new ApiFailure(409, "DUPLICATE_SKU", "SKU đã được sử dụng.");
            if (sql.Message.Contains("UQ_ProductVariants_Product_Size_Color", StringComparison.Ordinal))
                throw new ApiFailure(409, "DUPLICATE_VARIANT", "Tổ hợp kích thước và màu đã tồn tại.");
            throw Duplicate();
        }
    }

    public static async Task<string> Slug(string name, IQueryable<string> existing, CancellationToken ct)
    {
        var normalized = name.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var letters = new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        var stem = Regex.Replace(letters, "[^a-z0-9]+", "-").Trim('-');
        if (stem.Length == 0) stem = "item";
        var slug = stem;
        for (var i = 2; await existing.AnyAsync(s => s == slug, ct); i++) slug = $"{stem}-{i}";
        return slug;
    }
}
