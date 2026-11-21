using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SportShop.Api.Tests;

public sealed partial class CustomerOrderTests
{
    private const string Me = "/api/v1/users/me";
    private const string AddressBook = Me + "/addresses";

    private static object AddressBody(string line, bool? isDefault = null) => new
    {
        recipientName = " Người nhận ", phoneNumber = " 0912345678 ", province = "Hà Nội",
        district = "Hoàn Kiếm", ward = "Hàng Bạc", addressLine = line, isDefault
    };

    private static async Task<JsonElement> CreateAddress(Shop shop, Actor actor, string line, bool? isDefault = null)
    {
        var reply = await shop.SendTo(actor, HttpMethod.Post, AddressBook, AddressBody(line, isDefault));
        Assert.Equal(201, reply.Status);
        return reply.Data;
    }

    [SqlDevelopmentFact]
    public Task BothRolesCanUpdateOnlyAllowedProfileFields() => Run(async shop =>
    {
        var customer = await shop.Account("buyer");
        var admin = await shop.Account("admin", "Admin");
        var body = new { fullName = "  Nguyễn Văn An  ", phoneNumber = "  +84912345678  ", dateOfBirth = "2000-05-20", gender = "Other" };
        (await shop.SendTo(null, HttpMethod.Put, Me, body)).Error(401, "UNAUTHORIZED");
        foreach (var (actor, role, email) in new[] { (customer, "Customer", "buyer"), (admin, "Admin", "admin") })
        {
            var reply = await shop.SendTo(actor, HttpMethod.Put, Me + "?userId=2147483647", body);
            Assert.Equal(200, reply.Status);
            Assert.Equal(email + "@example.test", reply.Data.GetProperty("email").GetString());
            Assert.Equal(role, reply.Data.GetProperty("role").GetString());
            Assert.Equal("Nguyễn Văn An", reply.Data.GetProperty("fullName").GetString());
            Assert.Equal("+84912345678", reply.Data.GetProperty("phoneNumber").GetString());
            Assert.Equal("2000-05-20", reply.Data.GetProperty("dateOfBirth").GetString());
            Assert.Equal("Other", reply.Data.GetProperty("gender").GetString());
            foreach (var field in new[] { "passwordHash", "mustChangePassword", "tokenVersion", "isActive" })
                Assert.False(reply.Data.TryGetProperty(field, out _));
        }
        foreach (var field in new[] { "userId", "email", "role", "roleId", "isActive", "passwordHash", "tokenVersion", "mustChangePassword" })
            (await shop.SendTo(customer, HttpMethod.Put, Me, new Dictionary<string, object> {
                ["fullName"] = "Name", [field] = "forbidden" })).Error(400, "VALIDATION_ERROR");
        foreach (var invalid in new object[] { new { fullName = " A " }, new { fullName = new string('x', 101) },
            new { fullName = "Name", phoneNumber = "123" }, new { fullName = "Name", dateOfBirth = "3000-01-01" },
            new { fullName = "Name", dateOfBirth = "1800-01-01" }, new { fullName = "Name", gender = "invalid" } })
            (await shop.SendTo(customer, HttpMethod.Put, Me, invalid)).Error(400, "VALIDATION_ERROR");
        Assert.Equal("Nguyễn Văn An", (await shop.SendTo(customer, HttpMethod.Get, Me)).Data.GetProperty("fullName").GetString());
        Assert.True(await shop.Db.Users.AllAsync(u => u.TokenVersion == 0 && !u.MustChangePassword && u.IsActive));

        await shop.Db.Users.Where(u => u.Email == "admin@example.test")
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.MustChangePassword, true));
        (await shop.SendTo(admin, HttpMethod.Put, Me, body)).Error(403, "PASSWORD_CHANGE_REQUIRED");
        (await shop.SendTo(admin, HttpMethod.Get, AddressBook)).Error(403, "PASSWORD_CHANGE_REQUIRED");
    });

    [SqlDevelopmentFact]
    public Task AddressCrudScopesBothRolesAndKeepsOrderSnapshot() => Run(async shop =>
    {
        var a = await shop.Account("buyer");
        var b = await shop.Account("other");
        var admin = await shop.Account("admin", "Admin");
        (await shop.SendTo(null, HttpMethod.Get, AddressBook)).Error(401, "UNAUTHORIZED");
        (await shop.SendTo(a, HttpMethod.Post, AddressBook, AddressBody("   "))).Error(400, "VALIDATION_ERROR");
        var first = await CreateAddress(shop, a, " 12 Hàng Gai ", false);
        var firstId = first.GetProperty("addressId").GetInt32();
        var firstPath = $"{AddressBook}/{firstId}";
        Assert.True(first.GetProperty("isDefault").GetBoolean());
        Assert.Equal("12 Hàng Gai", first.GetProperty("addressLine").GetString());
        Assert.Equal("0912345678", first.GetProperty("phoneNumber").GetString());
        var second = await CreateAddress(shop, a, "Second");
        Assert.False(second.GetProperty("isDefault").GetBoolean());
        var secondPath = $"{AddressBook}/{second.GetProperty("addressId").GetInt32()}";
        foreach (var outsider in new[] { b, admin })
        {
            var list = await shop.SendTo(outsider, HttpMethod.Get, AddressBook + "?userId=1");
            Assert.Empty(list.Data.GetProperty("items").EnumerateArray());
            (await shop.SendTo(outsider, HttpMethod.Put, firstPath, AddressBody("Changed", true))).Error(404, "NOT_FOUND");
            (await shop.SendTo(outsider, HttpMethod.Delete, firstPath)).Error(404, "NOT_FOUND");
        }
        var adminAddress = await CreateAddress(shop, admin, "Admin");
        var adminPath = $"{AddressBook}/{adminAddress.GetProperty("addressId").GetInt32()}";
        Assert.Equal(200, (await shop.SendTo(admin, HttpMethod.Put, adminPath, AddressBody("Admin edited"))).Status);
        Assert.Equal("Admin edited", (await shop.SendTo(admin, HttpMethod.Get, AddressBook)).Data.GetProperty("items")[0]
            .GetProperty("addressLine").GetString());
        (await shop.SendTo(a, HttpMethod.Delete, adminPath)).Error(404, "NOT_FOUND");
        Assert.Equal(200, (await shop.SendTo(admin, HttpMethod.Delete, adminPath)).Status);

        await shop.Fill(a, await shop.Variant("sku", 50, 2));
        var checkout = await shop.Send(a, HttpMethod.Post, input: new {
            recipientName = first.GetProperty("recipientName").GetString(), recipientPhone = first.GetProperty("phoneNumber").GetString(),
            shippingAddress = first.GetProperty("addressLine").GetString(), paymentMethodId = 1 });
        Assert.Equal(201, checkout.Status);
        Assert.Equal(200, (await shop.SendTo(a, HttpMethod.Put, firstPath, AddressBody("New"))).Status);
        var removed = await shop.SendTo(a, HttpMethod.Delete, firstPath);
        Assert.Equal(200, removed.Status);
        Assert.Equal(JsonValueKind.Null, removed.Data.ValueKind);
        Assert.False((await shop.SendTo(a, HttpMethod.Get, AddressBook)).Data.GetProperty("items")[0].GetProperty("isDefault").GetBoolean());
        Assert.True((await shop.SendTo(a, HttpMethod.Put, secondPath, AddressBody("Second", true))).Data.GetProperty("isDefault").GetBoolean());
        Assert.Equal(200, (await shop.SendTo(a, HttpMethod.Delete, secondPath)).Status);
        Assert.True((await CreateAddress(shop, a, "First again", false)).GetProperty("isDefault").GetBoolean());
        var original = await shop.Db.Orders.AsNoTracking().SingleAsync();
        Assert.Equal("12 Hàng Gai", original.ShippingAddress);
        Assert.Equal("Người nhận", original.RecipientName);
        Assert.Equal("0912345678", original.RecipientPhone);
    });

    [SqlDevelopmentFact]
    public Task ConcurrentAddressWritersKeepSingleDefaultAndSupportPaging() => Run(async shop =>
    {
        var a = await shop.Account("buyer");
        const string ownerLock = "FROM dbo.Users WITH (UPDLOCK, HOLDLOCK)";
        shop.Gate.Arm(ownerLock, 2);
        var creation = Task.WhenAll(CreateAddress(shop, a, "First"), CreateAddress(shop, a, "Second"));
        try { await shop.Gate.Wait(); } finally { shop.Gate.Release(); }
        var addresses = await creation;
        Assert.Single(addresses, item => item.GetProperty("isDefault").GetBoolean());
        shop.Gate.Arm(ownerLock, 2);
        var updates = Task.WhenAll(addresses.Select(item => shop.SendTo(a, HttpMethod.Put,
            $"{AddressBook}/{item.GetProperty("addressId").GetInt32()}", AddressBody("Updated", true))));
        try { await shop.Gate.Wait(); } finally { shop.Gate.Release(); }
        Assert.All(await updates, reply => Assert.Equal(200, reply.Status));
        Assert.Equal(1, await shop.Db.UserAddresses.CountAsync(r => r.IsDefault));
        var page = (await shop.SendTo(a, HttpMethod.Get, AddressBook + "?page=1&pageSize=1&sort=-isDefault")).Data;
        Assert.Equal(2, page.GetProperty("pagination").GetProperty("totalItems").GetInt32());
        Assert.Equal(2, page.GetProperty("pagination").GetProperty("totalPages").GetInt32());
        Assert.True(Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("isDefault").GetBoolean());
        var remaining = (await shop.SendTo(a, HttpMethod.Get, AddressBook + "?page=2&pageSize=1&sort=-isDefault")).Data;
        Assert.False(Assert.Single(remaining.GetProperty("items").EnumerateArray()).GetProperty("isDefault").GetBoolean());
        foreach (var query in new[] { "?page=0", "?pageSize=51", "?sort=bad" })
            (await shop.SendTo(a, HttpMethod.Get, AddressBook + query)).Error(400, "VALIDATION_ERROR");
    });
}
