using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Auth;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;
using SportShop.Api.Middleware;

namespace SportShop.Api.Admin;

public sealed class CustomerQuery : PageQuery
{
    public string? Keyword { get; set; }
    public bool? IsActive { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CustomerStatusInput
{
    [JsonRequired] public bool IsActive { get; init; }
}

public sealed class CustomerData(SportShopDbContext db)
{
    private IQueryable<User> Customers => db.Users.AsNoTracking().Where(u => u.Role.RoleName == "Customer");

    internal Task<object> List(CustomerQuery query, CancellationToken ct)
    {
        var rows = Customers;
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            rows = rows.Where(u => u.FullName.Contains(keyword) || u.Email.Contains(keyword) ||
                (u.PhoneNumber != null && u.PhoneNumber.Contains(keyword)));
        }
        if (query.IsActive is bool active) rows = rows.Where(u => u.IsActive == active);
        return query.Read(rows, u => new
        {
            u.UserId, u.FullName, u.Email, u.PhoneNumber, u.IsActive,
            CreatedAt = DateTime.SpecifyKind(u.CreatedAt, DateTimeKind.Utc)
        }, "UserId", "-CreatedAt", ct, "FullName", "Email", "CreatedAt", "IsActive");
    }

    internal async Task<object> Detail(int userId, CancellationToken ct) =>
        await Customers.Where(u => u.UserId == userId).Select(u => new
        {
            u.UserId, u.FullName, u.Email, u.PhoneNumber, u.DateOfBirth, u.Gender, u.IsActive,
            CreatedAt = DateTime.SpecifyKind(u.CreatedAt, DateTimeKind.Utc),
            LastLoginAt = u.LastLoginAt.HasValue ? DateTime.SpecifyKind(u.LastLoginAt.Value, DateTimeKind.Utc) : (DateTime?)null,
            TotalOrders = u.Orders.Count(),
            TotalSpent = u.Orders.Where(o => o.Status.StatusCode == "DELIVERED").Sum(o => o.TotalAmount)
        }).SingleOrDefaultAsync(ct) ?? throw Missing();

    internal async Task<object> SetStatus(int userId, bool isActive, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var customer = await AccountSessions.LockById(db, userId).SingleOrDefaultAsync(ct);
                if (customer?.Role.RoleName != "Customer") throw Missing();

                var now = DateTime.UtcNow;
                if (customer.IsActive != isActive)
                {
                    customer.IsActive = isActive;
                    customer.UpdatedAt = now;
                    await db.SaveChangesAsync(ct);
                }
                if (!isActive)
                    await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, t => t.CreatedAt > now ? t.CreatedAt : now), ct);

                await tx.CommitAsync(ct);
                return new { customer.UserId, customer.IsActive };
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException || ex.GetBaseException() is SqlException { Number: 1205 })
            {
                db.ChangeTracker.Clear();
                if (attempt >= 3) throw Catalog.Conflict();
            }
        }
    }

    private static ApiFailure Missing() => new(404, "NOT_FOUND", "Không tìm thấy khách hàng.");
}
