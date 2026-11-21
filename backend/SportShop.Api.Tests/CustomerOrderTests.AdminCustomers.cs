using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data.Entities;
using Xunit;

namespace SportShop.Api.Tests;

public sealed partial class CustomerOrderTests
{
    private const string Customers = "/api/v1/admin/customers";
    private const string TestPassword = "TestCustomer123";

    private static Task<Reply> SetCustomerStatus(Shop shop, Actor? admin, int id, bool active) =>
        shop.SendTo(admin, HttpMethod.Put, $"{Customers}/{id}/status", new { isActive = active });

    private static async Task<User> SeedCustomerPassword(Shop shop)
    {
        var user = await shop.Db.Users.SingleAsync(u => u.Email == "buyer@example.test");
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, TestPassword);
        await shop.Db.SaveChangesAsync();
        return user;
    }

    private static Task<Reply> LoginCustomer(Shop shop) => shop.SendTo(null, HttpMethod.Post, "/api/v1/auth/login",
        new { email = "buyer@example.test", password = TestPassword });

    private static Task<Reply> RefreshCustomer(Shop shop, string cookie) =>
        shop.SendTo(null, HttpMethod.Post, "/api/v1/auth/refresh", new { }, cookie);

    private static void AssertSafeCustomer(JsonElement value)
    {
        foreach (var field in new[] { "passwordHash", "refreshTokenHash", "refreshTokens", "tokenVersion", "mustChangePassword" })
            Assert.False(value.TryGetProperty(field, out _));
    }

    [SqlDevelopmentFact]
    public Task AdminCustomersReadOnlyCustomerAccountsAndDeliveredSpend() => Run(async shop =>
    {
        var buyer = await shop.Account("buyer");
        await shop.Account("other");
        var admin = await shop.Account("admin", "Admin");
        var buyerId = await shop.Db.Users.Where(u => u.Email == "buyer@example.test").Select(u => u.UserId).SingleAsync();
        var adminId = await shop.Db.Users.Where(u => u.Email == "admin@example.test").Select(u => u.UserId).SingleAsync();
        await shop.Db.Users.Where(u => u.UserId == buyerId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.FullName, "Nguyễn Văn An").SetProperty(u => u.PhoneNumber, "0912345678"));
        foreach (var (actor, status, code) in new[] { ((Actor?)null, 401, "UNAUTHORIZED"), (buyer, 403, "FORBIDDEN") })
        {
            (await shop.SendTo(actor, HttpMethod.Get, Customers)).Error(status, code);
            (await shop.SendTo(actor, HttpMethod.Get, $"{Customers}/{buyerId}")).Error(status, code);
            (await SetCustomerStatus(shop, actor, buyerId, false)).Error(status, code);
        }
        (await shop.SendTo(admin, HttpMethod.Get, $"{Customers}/{adminId}")).Error(404, "NOT_FOUND");
        (await SetCustomerStatus(shop, admin, adminId, false)).Error(404, "NOT_FOUND");
        (await SetCustomerStatus(shop, admin, int.MaxValue, false)).Error(404, "NOT_FOUND");
        (await shop.SendTo(admin, HttpMethod.Get, $"{Customers}/{int.MaxValue}")).Error(404, "NOT_FOUND");
        foreach (var input in new object[] { new { }, new { isActive = (bool?)null }, new { isActive = true, role = "Admin" } })
            (await shop.SendTo(admin, HttpMethod.Put, $"{Customers}/{buyerId}/status", input)).Error(400, "VALIDATION_ERROR");

        var sku = await shop.Variant("sku", 50, 3);
        var shipped = await Buy(shop, buyer, sku);
        await Buy(shop, buyer, sku);
        foreach (var step in new[] { "CONFIRMED", "SHIPPING", "DELIVERED" })
            Success(await Change(shop, admin, shipped, step), step);
        foreach (var keyword in new[] { "nguy%E1%BB%85n", "buyer%40example.test", "123456" })
        {
            var list = await shop.SendTo(admin, HttpMethod.Get, $"{Customers}?keyword={keyword}&isActive=true");
            var item = Assert.Single(list.Data.GetProperty("items").EnumerateArray());
            Assert.Equal(buyerId, item.GetProperty("userId").GetInt32());
            AssertSafeCustomer(item);
        }
        var all = (await shop.SendTo(admin, HttpMethod.Get, Customers + "?pageSize=1&sort=email")).Data;
        Assert.Equal(2, all.GetProperty("pagination").GetProperty("totalItems").GetInt32());
        Assert.Equal(buyerId, Assert.Single(all.GetProperty("items").EnumerateArray()).GetProperty("userId").GetInt32());
        var pageTwo = (await shop.SendTo(admin, HttpMethod.Get, Customers + "?page=2&pageSize=1&sort=email")).Data;
        Assert.Equal("other@example.test", pageTwo.GetProperty("items")[0].GetProperty("email").GetString());
        Assert.Equal(200, (await SetCustomerStatus(shop, admin, buyerId, false)).Status);
        var inactive = (await shop.SendTo(admin, HttpMethod.Get, Customers + "?isActive=false")).Data;
        Assert.Equal(buyerId, Assert.Single(inactive.GetProperty("items").EnumerateArray()).GetProperty("userId").GetInt32());
        var detail = (await shop.SendTo(admin, HttpMethod.Get, $"{Customers}/{buyerId}")).Data;
        AssertSafeCustomer(detail);
        Assert.Equal(2, detail.GetProperty("totalOrders").GetInt32());
        Assert.Equal(30050m, detail.GetProperty("totalSpent").GetDecimal());
        Assert.True(await shop.Db.Users.Where(u => u.UserId == adminId).Select(u => u.IsActive).SingleAsync());
    });

    [SqlDevelopmentFact]
    public Task CustomerStatusRevokesSessionsOnlyOnLockAndPreservesData() => Run(async shop =>
    {
        var buyer = await shop.Account("buyer");
        var admin = await shop.Account("admin", "Admin");
        var source = await SeedCustomerPassword(shop);
        var sku = await shop.Variant("sku", 50, 3);
        await Buy(shop, buyer, sku);
        await shop.Fill(buyer, sku);
        var first = await LoginCustomer(shop);
        var second = await LoginCustomer(shop);
        Assert.Equal(200, first.Status);
        Assert.Equal(200, second.Status);
        var access = new Actor(buyer.CartId, first.Data.GetProperty("accessToken").GetString()!);
        var before = await Snapshot(shop);
        var ownTokens = shop.Db.RefreshTokens.Where(r => r.UserId == source.UserId).OrderBy(r => r.RefreshTokenId);
        var issued = await ownTokens.CountAsync();
        Assert.True(issued >= 2);
        var locked = await SetCustomerStatus(shop, admin, source.UserId, false);
        Assert.False(locked.Data.GetProperty("isActive").GetBoolean());
        var lockedUser = await shop.Db.Users.AsNoTracking().SingleAsync(u => u.UserId == source.UserId);
        Assert.Equal(source.TokenVersion, lockedUser.TokenVersion);
        Assert.Equal(source.PasswordHash, lockedUser.PasswordHash);
        Assert.Equal(source.RoleId, lockedUser.RoleId);
        var revocations = await ownTokens.Select(r => r.RevokedAt).ToArrayAsync();
        Assert.Equal(issued, revocations.Length);
        Assert.All(revocations, date => Assert.NotNull(date));
        Assert.False(await shop.Db.RefreshTokens.AnyAsync(r => r.UserId != source.UserId && r.RevokedAt != null));
        Assert.Equal(200, (await SetCustomerStatus(shop, admin, source.UserId, false)).Status);
        Assert.Equal(lockedUser.UpdatedAt, await shop.Db.Users.Where(u => u.UserId == source.UserId).Select(u => u.UpdatedAt).SingleAsync());
        Assert.Equal(revocations, await ownTokens.Select(r => r.RevokedAt).ToArrayAsync());
        (await LoginCustomer(shop)).Error(403, "ACCOUNT_LOCKED");
        (await RefreshCustomer(shop, first.Cookie!)).Error(401, "UNAUTHORIZED");
        (await RefreshCustomer(shop, second.Cookie!)).Error(401, "UNAUTHORIZED");
        Assert.Equal(200, (await shop.SendTo(access, HttpMethod.Get, Me)).Status);
        Assert.Equal(1, await shop.Db.CartItems.CountAsync());
        Assert.Equal(before, await Snapshot(shop));

        Assert.True((await SetCustomerStatus(shop, admin, source.UserId, true)).Data.GetProperty("isActive").GetBoolean());
        (await RefreshCustomer(shop, first.Cookie!)).Error(401, "UNAUTHORIZED");
        Assert.Equal(200, (await LoginCustomer(shop)).Status);
        Assert.Equal(source.TokenVersion, await shop.Db.Users.Where(u => u.UserId == source.UserId).Select(u => u.TokenVersion).SingleAsync());
    });

    [SqlDevelopmentFact]
    public Task CustomerLockSerializesWithRefreshAndCompetingStatusWrites() => Run(async shop =>
    {
        await shop.Account("buyer");
        var admin = await shop.Account("admin", "Admin");
        var customer = await SeedCustomerPassword(shop);
        var login = await LoginCustomer(shop);
        Assert.Equal(200, login.Status);
        var ownTokens = shop.Db.RefreshTokens.Where(t => t.UserId == customer.UserId);
        var issued = await ownTokens.CountAsync();
        const string accountLock = "FROM dbo.Users WITH (UPDLOCK) WHERE UserId";
        shop.Gate.Arm(accountLock, 1);
        var refresh = RefreshCustomer(shop, login.Cookie!);
        try
        {
            await shop.Gate.Wait();
            var locks = await Task.WhenAll(SetCustomerStatus(shop, admin, customer.UserId, false),
                SetCustomerStatus(shop, admin, customer.UserId, false));
            Assert.All(locks, reply =>
            {
                Assert.Equal(200, reply.Status);
                Assert.False(reply.Data.GetProperty("isActive").GetBoolean());
            });
        }
        finally { shop.Gate.Release(); }
        (await refresh).Error(401, "UNAUTHORIZED");
        Assert.Equal(issued, await ownTokens.CountAsync());
        Assert.False(await ownTokens.AnyAsync(t => t.RevokedAt == null));
        Assert.False(await shop.Db.Users.Where(u => u.UserId == customer.UserId).Select(u => u.IsActive).SingleAsync());

        await SetCustomerStatus(shop, admin, customer.UserId, true);
        var fresh = await LoginCustomer(shop);
        Assert.Equal(200, fresh.Status);
        shop.Gate.Arm(accountLock, 1);
        var delayedLock = SetCustomerStatus(shop, admin, customer.UserId, false);
        Reply rotated;
        try
        {
            await shop.Gate.Wait();
            rotated = await RefreshCustomer(shop, fresh.Cookie!);
            Assert.Equal(200, rotated.Status);
        }
        finally { shop.Gate.Release(); }
        Assert.Equal(200, (await delayedLock).Status);
        Assert.False(await ownTokens.AnyAsync(t => t.RevokedAt == null));
        (await RefreshCustomer(shop, rotated.Cookie!)).Error(401, "UNAUTHORIZED");
    });
}
