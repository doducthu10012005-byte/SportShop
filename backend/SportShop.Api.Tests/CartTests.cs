using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SportShop.Api.Auth;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;
using SportShop.Api.ShoppingCart;
using Xunit;

namespace SportShop.Api.Tests;

public sealed class CartTests
{
    [Theory]
    [InlineData(true, 3, true)]
    [InlineData(true, 2, false)]
    [InlineData(true, 0, false)]
    [InlineData(false, 9, false)]
    public void TotalsUseWholePurchasableLines(bool selling, int stock, bool available)
    {
        var line = new CartLine(1, 1, 1, "Áo", "SKU", "M", "Đỏ", null, 12.50m, 3, stock) { Selling = selling };
        var cart = new CartView(1, [line]);
        Assert.Equal(available, line.IsAvailable);
        Assert.Single(cart.Items);
        Assert.Equal(37.50m, line.LineTotal);
        Assert.Equal(available ? 3L : 0L, cart.TotalItems);
        Assert.Equal(available ? 37.50m : 0m, cart.Subtotal);
    }

    // Rendezvous before executing the cart lock SQL on two distinct request connections.
    private sealed class WriteGate : DbCommandInterceptor
    {
        private TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int remaining;

        public void Arm()
        {
            ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref remaining, 2);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (Volatile.Read(ref remaining) > 0 && command.CommandText.Contains("UPDLOCK", StringComparison.Ordinal))
            {
                if (Interlocked.Decrement(ref remaining) == 0) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            }
            return result;
        }

        public bool BothArrived => ready.Task.IsCompletedSuccessfully;
    }

    private sealed class CartClient(HttpClient http, string? token)
    {
        public async Task<(int Status, JsonElement Body)> Send(HttpMethod method, string path = "", object? input = null)
        {
            using var request = new HttpRequestMessage(method, "/api/v1/cart" + path);
            if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (input != null) request.Content = JsonContent.Create(input);
            using var response = await http.SendAsync(request);
            return ((int)response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
        }

        public async Task<JsonElement> Call(HttpMethod method, string path = "", object? input = null,
            int status = 200, string? error = null)
        {
            var result = await Send(method, path, input);
            Assert.Equal(status, result.Status);
            Assert.Equal(error == null, result.Body.GetProperty("success").GetBoolean());
            if (error == null) return result.Body.GetProperty("data");
            Assert.Equal(error, result.Body.GetProperty("errorCode").GetString());
            Assert.False(string.IsNullOrWhiteSpace(result.Body.GetProperty("traceId").GetString()));
            return result.Body;
        }
    }

    [SqlDevelopmentFact]
    public Task CustomerCartsOnSqlServer() => TemporarySqlDatabase.Run(async (connection, options) =>
    {
        var gate = new WriteGate();
        using var root = new WebApplicationFactory<Program>();
        using var factory = root.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(log => log.ClearProviders());
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connection, ["AdminBootstrap:Enabled"] = "false",
                ["Jwt:SecretKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            }));
            builder.ConfigureServices(s => s.AddDbContext<SportShopDbContext>(o => o.AddInterceptors(gate)));
        });
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<SessionTokens>();
        await using var db = new SportShopDbContext(options);
        async Task<CartClient> Account(string name, string role)
        {
            var user = new User { Email = name + "@example.test", Role = await db.Roles.SingleAsync(r => r.RoleName == role),
                Cart = role == "Customer" ? new Cart() : null };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return new CartClient(http, tokens.Issue(user, DateTime.UtcNow).Body.AccessToken);
        }
        var a = await Account("first", "Customer");
        var b = await Account("second", "Customer");
        var admin = await Account("admin", "Admin");
        var guest = new CartClient(http, null);
        var (first, second) = await SeedVariants(db);
        var id = first.VariantId;
        var add = HttpMethod.Post;
        var put = HttpMethod.Put;
        var del = HttpMethod.Delete;
        var get = HttpMethod.Get;
        foreach (var (method, path) in new[] { (get, ""), (add, "/items"), (put, "/items/1"), (del, "/items/1"), (del, "") })
        {
            await guest.Call(method, path, status: 401, error: "UNAUTHORIZED");
            await admin.Call(method, path, status: 403, error: "FORBIDDEN");
        }

        Assert.Empty((await a.Call(get)).GetProperty("items").EnumerateArray());
        foreach (var quantity in new[] { 0, -1, 100 })
            await a.Call(add, "/items", new { variantId = id, quantity }, 400, "VALIDATION_ERROR");
        await a.Call(add, "/items", new { variantId = int.MaxValue, quantity = 1 }, 404, "NOT_FOUND");
        var start = await a.Call(add, "/items", new { variantId = id, quantity = 2 });
        var item = Assert.Single(start.GetProperty("items").EnumerateArray());
        var pathA = "/items/" + item.GetProperty("cartItemId").GetInt32();
        Assert.Equal("/uploads/color-first.jpg", item.GetProperty("imageUrl").GetString());
        Assert.Equal(2, start.GetProperty("totalItems").GetInt32());
        Assert.Equal(100m, start.GetProperty("subtotal").GetDecimal());
        var other = await b.Call(add, "/items", new { variantId = id, quantity = 5 });
        Assert.NotEqual(start.GetProperty("cartId").GetInt32(), other.GetProperty("cartId").GetInt32());
        await b.Call(put, pathA, new { quantity = 1 }, 404, "NOT_FOUND");
        await b.Call(del, pathA, status: 404, error: "NOT_FOUND");
        Assert.Equal(5, (await b.Call(get)).GetProperty("totalItems").GetInt32());
        var grown = await a.Call(add, "/items", new { variantId = id, quantity = 2 });
        Assert.Single(grown.GetProperty("items").EnumerateArray());
        Assert.Equal(4, grown.GetProperty("totalItems").GetInt32());
        var shortage = await a.Call(add, "/items", new { variantId = id, quantity = 2 }, 409, "INSUFFICIENT_STOCK");
        Assert.Equal($"variantId:{id}", shortage.GetProperty("errors")[0].GetProperty("field").GetString());
        await a.Call(put, pathA, new { quantity = 6 }, 409, "INSUFFICIENT_STOCK");
        Assert.Equal(4, (await a.Call(get)).GetProperty("totalItems").GetInt32());
        await a.Call(put, pathA, new { quantity = 3 });
        var mixed = await a.Call(add, "/items", new { variantId = second.VariantId, quantity = 1 });
        Assert.Equal(175m, mixed.GetProperty("subtotal").GetDecimal());
        var fallback = mixed.GetProperty("items")[1];
        Assert.Equal("/uploads/primary.jpg", fallback.GetProperty("imageUrl").GetString());

        first.Price = 60;
        await db.SaveChangesAsync();
        Assert.Equal(205m, (await a.Call(get)).GetProperty("subtotal").GetDecimal());
        foreach (var stock in new[] { 2, 0 })
        {
            first.Inventory!.QuantityOnHand = stock;
            await db.SaveChangesAsync();
            var cart = await a.Call(get);
            var unavailable = cart.GetProperty("items")[0];
            Assert.False(unavailable.GetProperty("isAvailable").GetBoolean());
            Assert.Equal(3, unavailable.GetProperty("quantity").GetInt32());
            Assert.Equal(stock, unavailable.GetProperty("availableStock").GetInt32());
            Assert.Equal(180m, unavailable.GetProperty("lineTotal").GetDecimal());
            Assert.Equal(1, cart.GetProperty("totalItems").GetInt32());
            Assert.Equal(25m, cart.GetProperty("subtotal").GetDecimal());
        }
        first.Inventory!.QuantityOnHand = 2;
        await db.SaveChangesAsync();
        Assert.Equal(3, (await a.Call(put, pathA, new { quantity = 2 })).GetProperty("totalItems").GetInt32());
        foreach (var hideProduct in new[] { true, false })
        {
            first.Product.IsActive = !hideProduct;
            first.IsActive = hideProduct;
            await db.SaveChangesAsync();
            await a.Call(put, pathA, new { quantity = 1 }, 404, "NOT_FOUND");
            await a.Call(add, "/items", new { variantId = id, quantity = 1 }, 404, "NOT_FOUND");
            var hiddenCart = await a.Call(get);
            Assert.Equal(2, hiddenCart.GetProperty("items").GetArrayLength());
            Assert.Equal(hideProduct ? 0 : 1, hiddenCart.GetProperty("totalItems").GetInt32());
            Assert.Equal(hideProduct ? 0m : 25m, hiddenCart.GetProperty("subtotal").GetDecimal());
            var unavailable = hiddenCart.GetProperty("items")[0];
            Assert.False(unavailable.GetProperty("isAvailable").GetBoolean());
            Assert.Equal(2, unavailable.GetProperty("quantity").GetInt32());
        }
        var afterDelete = await a.Call(del, pathA);
        Assert.Single(afterDelete.GetProperty("items").EnumerateArray());
        Assert.Equal(25m, afterDelete.GetProperty("subtotal").GetDecimal());
        Assert.Empty((await a.Call(del)).GetProperty("items").EnumerateArray());
        Assert.Single((await b.Call(get)).GetProperty("items").EnumerateArray());

        first.IsActive = true;
        first.Inventory.QuantityOnHand = 5;
        await db.SaveChangesAsync();
        gate.Arm();
        var added = await Task.WhenAll(a.Call(add, "/items", new { variantId = id, quantity = 2 }),
            a.Call(add, "/items", new { variantId = id, quantity = 2 }));
        Assert.True(gate.BothArrived);
        Assert.Equal(new[] { 2, 4 }, added.Select(c => c.GetProperty("totalItems").GetInt32()).Order());
        gate.Arm();
        var race = await Task.WhenAll(a.Send(add, "/items", new { variantId = id, quantity = 1 }),
            a.Send(add, "/items", new { variantId = id, quantity = 1 }));
        Assert.True(gate.BothArrived);
        Assert.Single(race, r => r.Status == 200);
        Assert.Equal("INSUFFICIENT_STOCK", Assert.Single(race, r => r.Status == 409).Body.GetProperty("errorCode").GetString());
        var final = await a.Call(get);
        var finalItem = Assert.Single(final.GetProperty("items").EnumerateArray());
        Assert.Equal(5, finalItem.GetProperty("quantity").GetInt32());
        first.Inventory.QuantityOnHand = 110;
        await db.SaveChangesAsync();
        var finalPath = "/items/" + finalItem.GetProperty("cartItemId").GetInt32();
        await a.Call(put, finalPath, new { quantity = 99 });
        await a.Call(add, "/items", new { variantId = id, quantity = 1 }, 400, "VALIDATION_ERROR");
        Assert.Equal(99, (await a.Call(get)).GetProperty("totalItems").GetInt32());
        gate.Arm();
        var update = a.Send(put, finalPath, new { quantity = 3 });
        var remove = a.Send(del, finalPath);
        await Task.WhenAll(update, remove);
        Assert.True(gate.BothArrived);
        var updated = await update;
        if (updated.Status == 404)
            Assert.Equal("NOT_FOUND", updated.Body.GetProperty("errorCode").GetString());
        else
        {
            Assert.Equal(200, updated.Status);
            Assert.Equal(3, updated.Body.GetProperty("data").GetProperty("totalItems").GetInt32());
        }
        Assert.Equal(200, (await remove).Status);
        Assert.Empty((await a.Call(get)).GetProperty("items").EnumerateArray());
        Assert.Equal(5, (await b.Call(get)).GetProperty("totalItems").GetInt32());
        Assert.Equal(110, await db.Inventories.Where(i => i.VariantId == id).Select(i => i.QuantityOnHand).SingleAsync());
        Assert.False(await db.InventoryTransactions.AnyAsync());
    });

    private static async Task<(ProductVariant, ProductVariant)> SeedVariants(SportShopDbContext db)
    {
        var product = new Product { ProductName = "Giày", Slug = "giay",
            Category = new Category { CategoryName = "Giày", Slug = "giay" },
            Brand = new Brand { BrandName = "Sport", Slug = "sport" } };
        var red = new Color { ColorName = "Đỏ" };
        var size = new Size { SizeName = "M" };
        var first = new ProductVariant { Product = product, Color = red, Size = size, Price = 50, Sku = "RED",
            Inventory = new Inventory { QuantityOnHand = 5 } };
        var second = new ProductVariant { Product = product, Color = new Color { ColorName = "Đen" }, Size = size,
            Price = 25, Sku = "BLACK", Inventory = new Inventory { QuantityOnHand = 2 } };
        product.Images.Add(new ProductImage { ImageUrl = "/uploads/primary.jpg", IsPrimary = true });
        product.Images.Add(new ProductImage { ImageUrl = "/uploads/color-second.jpg", Color = red, SortOrder = 2 });
        product.Images.Add(new ProductImage { ImageUrl = "/uploads/color-first.jpg", Color = red, SortOrder = 1 });
        db.ProductVariants.AddRange(first, second);
        await db.SaveChangesAsync();
        return (first, second);
    }
}
