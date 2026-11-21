using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SportShop.Api.Admin;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;
using SportShop.Api.Middleware;
using Xunit;

namespace SportShop.Api.Tests;

public sealed class MasterDataSqlTests
{
    [Theory]
    [InlineData("Import", 5, 6, 11)]
    [InlineData("Adjustment", 5, 0, 0)]
    [InlineData("Adjustment", 5, 2, 2)]
    public void StockBalanceUsesDeclaredOperation(string type, int before, int quantity, int after) =>
        Assert.Equal(after, StockData.Balance(before, new StockInput { Type = type, Quantity = quantity, Note = "Kiểm kê" }));

    [Theory]
    [InlineData("Sale", 2, 1, "sale")]
    [InlineData("Import", 2, 0, null)]
    [InlineData("Import", int.MaxValue, 1, null)]
    [InlineData("Adjustment", 3, 3, "kiểm kê")]
    [InlineData("Adjustment", 3, 2, " ")]
    public void RejectsInvalidStockMovement(string type, int before, int quantity, string? note) =>
        Assert.Equal("VALIDATION_ERROR", Assert.Throws<ApiFailure>(() =>
            StockData.Balance(before, new StockInput { Type = type, Quantity = quantity, Note = note })).Code);

    // Do not let either HTTP writer take its SQL application lock before the other has reached it.
    private sealed class CatalogGate : DbCommandInterceptor
    {
        public int Remaining;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Volatile.Read(ref Remaining) > 0 && command.CommandText.Contains("sp_getapplock"))
            {
                if (Interlocked.Decrement(ref Remaining) == 0) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            }
            return result;
        }
    }

    // Force real RowVersion changes from a separate SQL connection immediately before EF's UPDATE.
    private sealed class StockConflict(DbContextOptions<SportShopDbContext> options) : SaveChangesInterceptor
    {
        public int Remaining;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            var inventory = data.Context!.ChangeTracker.Entries<Inventory>()
                .SingleOrDefault(e => e.State == EntityState.Modified);
            if (inventory != null && Remaining > 0)
            {
                Remaining--;
                await using var other = new SportShopDbContext(options);
                await other.Inventories.Where(i => i.InventoryId == inventory.Entity.InventoryId)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.UpdatedAt, DateTime.UtcNow), ct);
            }
            return result;
        }
    }

    [SqlDevelopmentFact]
    public Task AdminMasterDataOnSqlServer() => TemporarySqlDatabase.Run(Verify);

    private static async Task Verify(string connection, DbContextOptions<SportShopDbContext> options)
    {
        var gate = new CatalogGate();
        var conflicts = new StockConflict(options);
        var directory = Directory.CreateTempSubdirectory("SportShopPhotos-");
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        try
        {
            using var root = new WebApplicationFactory<Program>();
            using var factory = root.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseWebRoot(directory.FullName);
                builder.ConfigureLogging(log => log.ClearProviders());
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = connection,
                    ["Payment:ExpiryJob:Enabled"] = "false",
                    ["Jwt:SecretKey"] = key,
                    ["Jwt:Issuer"] = "master-tests",
                    ["Jwt:Audience"] = "master-tests",
                    ["AdminBootstrap:Enabled"] = "false"
                }));
                builder.ConfigureServices(s => s.AddDbContext<SportShopDbContext>(o => o.AddInterceptors(gate, conflicts)));
            });
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            async Task<string> Bearer(string role)
            {
                await using var db = new SportShopDbContext(options);
                var user = new User
                {
                    Email = role + "@example.test",
                    Role = await db.Roles.SingleAsync(r => r.RoleName == role),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.Users.Add(user);
                await db.SaveChangesAsync();
                return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = "master-tests",
                    Audience = "master-tests",
                    Expires = DateTime.UtcNow.AddMinutes(10),
                    Claims = new Dictionary<string, object> { ["sub"] = user.UserId.ToString(), ["role"] = role, ["tv"] = 0 },
                    SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)
                });
            }

            var admin = await Bearer("Admin");
            var customer = await Bearer("Customer");
            async Task<HttpResponseMessage> Send(string method, string route, object? body, string? token)
            {
                using var message = new HttpRequestMessage(new HttpMethod(method), "/api/v1/admin/" + route);
                if (token != null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (body is HttpContent content) message.Content = content;
                else if (body != null) message.Content = JsonContent.Create(body);
                return await client.SendAsync(message);
            }
            async Task<JsonElement> Call(string method, string path, object? body = null, int status = 200, string? error = null)
            {
                using var response = await Send(method, path, body, admin);
                Assert.Equal(status, (int)response.StatusCode);
                var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (error == null) return json.GetProperty("data");
                Assert.Equal(error, json.GetProperty("errorCode").GetString());
                return json;
            }
            var descriptions = factory.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
                .ApiDescriptionGroups.Items.SelectMany(g => g.Items)
                .Where(a => a.ActionDescriptor.RouteValues["controller"] is
                    "AdminLookups" or "AdminProducts" or "AdminImages" or "AdminInventory").ToList();
            Assert.Equal(29, descriptions.Count);
            foreach (var endpoint in descriptions)
            {
                var route = System.Text.RegularExpressions.Regex.Replace(endpoint.RelativePath![13..], @"\{[^}]+\}", "1");
                foreach (var (token, status) in new[] { ((string?)null, 401), (customer, 403) })
                {
                    using var response = await Send(endpoint.HttpMethod!, route, null, token);
                    Assert.Equal(status, (int)response.StatusCode);
                }
            }

            var brand = (await Call("POST", "brands", new { brandName = "Nike" }, 201)).GetProperty("brandId").GetInt32();
            var category = (await Call("POST", "categories", new { categoryName = "Giày" }, 201)).GetProperty("categoryId").GetInt32();
            var size = (await Call("POST", "sizes", new { sizeName = "M" }, 201)).GetProperty("sizeId").GetInt32();
            var color = (await Call("POST", "colors", new { colorName = "Đỏ", hexCode = "#D00000" }, 201)).GetProperty("colorId").GetInt32();
            await Call("POST", "brands", new { brandName = " NIKE " }, 409, "DUPLICATE_NAME");
            await Call("PUT", $"categories/{category}", new { categoryName = "Giày", parentCategoryId = category }, 400, "VALIDATION_ERROR");
            await Call("POST", "colors", new { colorName = "X", hexCode = "bad" }, 400, "VALIDATION_ERROR");
            await Call("POST", "colors", new { colorName = "X", hexCode = "" }, 400, "VALIDATION_ERROR");
            Assert.Equal(JsonValueKind.Array, (await Call("GET", "categories")).ValueKind);
            Assert.Equal(1, (await Call("GET", "brands?pageSize=1&sort=-brandName")).GetProperty("pagination").GetProperty("totalItems").GetInt32());
            object Variant(string sku, int c, int stock = 5) => new { sizeId = size, colorId = c, sku, price = 120000, initialStock = stock };
            object Product(string name, params object[] variants) => new { productName = name, brandId = brand, categoryId = category, variants };
            gate.Remaining = 2;
            var pair = await Task.WhenAll(
                Send("POST", "products", Product("Giày chạy", Variant("SKU-A", color)), admin),
                Send("POST", "products", Product("Giày chạy", Variant("SKU-B", color)), admin));
            Assert.Equal(0, gate.Remaining);
            using var created = Assert.Single(pair, r => (int)r.StatusCode == 201);
            using var duplicate = Assert.Single(pair, r => (int)r.StatusCode == 409);
            Assert.Equal("DUPLICATE_NAME", (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
            var data = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            var id = data.GetProperty("productId").GetInt32();
            var initial = data.GetProperty("variants")[0];
            var vid = initial.GetProperty("variantId").GetInt32();
            var sku = initial.GetProperty("sku").GetString()!;
            await using var check = new SportShopDbContext(options);
            Assert.Equal(1, await check.Products.CountAsync());
            Assert.Equal(1, await check.Inventories.CountAsync());
            Assert.Equal(1, await check.InventoryTransactions.CountAsync());
            await Call("POST", "products", Product("Rollback", Variant("GOOD", color), Variant("BAD", int.MaxValue)), 400, "VALIDATION_ERROR");
            await Call("POST", "products", Product("Rollback", Variant(sku, color)), 409, "DUPLICATE_SKU");
            Assert.False(await check.Products.AnyAsync(p => p.ProductName == "Rollback"));
            await Call("POST", $"products/{id}/variants", Variant("NEW", color), 409, "DUPLICATE_VARIANT");
            await Call("DELETE", $"brands/{brand}", null, 409, "IN_USE");
            await Call("DELETE", $"sizes/{size}", null, 409, "IN_USE");
            var distinct = await Call("POST", "products", Product("Giay chay", Variant("OTHER", color, 0)), 201);
            var other = distinct.GetProperty("productId").GetInt32();
            await Call("DELETE", $"products/{id}");
            await Call("PUT", $"products/{other}", new { productName = "  GIÀY CHẠY  ", brandId = brand, categoryId = category }, 409, "DUPLICATE_NAME");
            var hidden = await Call("GET", $"products?isActive=false&keyword={sku}&brandId={brand}&categoryId={category}&sort=-createdAt");
            Assert.Equal(id, Assert.Single(hidden.GetProperty("items").EnumerateArray()).GetProperty("productId").GetInt32());
            await Call("GET", "products?pageSize=51", null, 400, "VALIDATION_ERROR");
            await Call("GET", "products?sort=unknown", null, 400, "VALIDATION_ERROR");
            var child = (await Call("POST", "categories", new { categoryName = "Con", parentCategoryId = category }, 201)).GetProperty("categoryId").GetInt32();
            await Call("PUT", $"categories/{category}", new { categoryName = "Giày", parentCategoryId = child }, 400, "VALIDATION_ERROR");
            var newColor = (await Call("POST", "colors", new { colorName = "Đen" }, 201)).GetProperty("colorId").GetInt32();
            var extra = await Call("POST", $"products/{other}/variants", Variant("THIRD", newColor, 0), 201);
            var variantPath = $"products/{other}/variants/{extra.GetProperty("variantId").GetInt32()}";
            await Call("PUT", variantPath, new { sku = "THIRD", price = 123, quantityOnHand = 500 }, 400, "VALIDATION_ERROR");
            await Call("PUT", variantPath, new { sku = "THIRD", price = -1 }, 400, "VALIDATION_ERROR");
            await Call("PUT", variantPath, new { sku = "THIRD-NEW", price = 123 });
            await Call("DELETE", variantPath);
            await Call("DELETE", $"colors/{newColor}");
            await Call("PUT", variantPath, new { sku = "THIRD-NEW", price = 123, isActive = true }, 400, "VALIDATION_ERROR");
            await Call("POST", "colors", new { colorName = "Đen" }, 409, "DUPLICATE_NAME");
            await Call("PUT", $"colors/{newColor}", new { colorName = "Đen", isActive = true });
            await Call("PUT", variantPath, new { sku = "THIRD-NEW", price = 123, isActive = true });

            var stock = await Call("GET", "inventory?lowStock=true&keyword=" + sku);
            Assert.True(stock.GetProperty("items")[0].GetProperty("isLowStock").GetBoolean());
            var adjustment = $"inventory/{vid}/adjustments";
            await Call("POST", adjustment, new { type = "Adjustment", quantity = 5, note = "Kiểm kê" }, 400, "VALIDATION_ERROR");
            conflicts.Remaining = 1;
            await Call("POST", adjustment, new { type = "Import", quantity = 1 });
            Assert.Equal(0, conflicts.Remaining);
            conflicts.Remaining = 4;
            await Call("POST", adjustment, new { type = "Import", quantity = 2 }, 409, "CONCURRENCY_CONFLICT");
            Assert.Equal(6, await check.Inventories.Where(i => i.VariantId == vid).Select(i => i.QuantityOnHand).SingleAsync());
            Assert.Equal(2, await check.InventoryTransactions.CountAsync(t => t.VariantId == vid));
            Assert.Empty((await Call("GET", "inventory?lowStock=true&keyword=" + sku)).GetProperty("items").EnumerateArray());
            await Call("POST", adjustment, new { type = "Adjustment", quantity = 3, note = "Kiểm kê" });
            var ledger = await Call("GET", $"inventory/{vid}/transactions?pageSize=1&sort=-createdAt");
            Assert.Equal(-3, ledger.GetProperty("items")[0].GetProperty("quantityChange").GetInt32());
            Assert.EndsWith("Z", ledger.GetProperty("items")[0].GetProperty("createdAt").GetString());
            await VerifyPhotos(client, id, directory.FullName, Call);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task VerifyPhotos(HttpClient client, int id, string root,
        Func<string, string, object?, int, string?, Task<JsonElement>> call)
    {
        using var bmp = new SkiaSharp.SKBitmap(2, 2);
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var png = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        var photoPath = $"products/{id}/images";
        MultipartFormDataContent Form(byte[] bytes, bool primary = false, string filename = "test.png")
        {
            var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(bytes), "ImageFile", filename);
            if (primary) form.Add(new StringContent("true"), "isPrimary");
            return form;
        }
        await call("POST", photoPath, Form([0, 1, 2]), 415, "UNSUPPORTED_MEDIA_TYPE");
        await call("POST", photoPath, Form(png.ToArray()[..12]), 400, "VALIDATION_ERROR");
        await call("POST", photoPath, Form(new byte[ProductPhotos.FileLimit + 1]), 413, "PAYLOAD_TOO_LARGE");
        await call("POST", photoPath, Form(new byte[ProductPhotos.FileLimit + 128 * 1024]), 413, "PAYLOAD_TOO_LARGE");
        await call("POST", photoPath, Form(png.ToArray(), filename: "test.jpg"), 415, "UNSUPPORTED_MEDIA_TYPE");
        var first = await call("POST", photoPath, Form(png.ToArray()), 201, null);
        Assert.False(first.GetProperty("isPrimary").GetBoolean());
        var second = await call("POST", photoPath, Form(png.ToArray(), true), 201, null);
        var third = await call("POST", photoPath, Form(png.ToArray(), true), 201, null);
        Assert.Equal(2, third.GetProperty("sortOrder").GetInt32());
        var url = third.GetProperty("imageUrl").GetString()!;
        using var response = await client.GetAsync(url);
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var detail = await call("GET", $"products/{id}", null, 200, null);
        Assert.Single(detail.GetProperty("images").EnumerateArray(), i => i.GetProperty("isPrimary").GetBoolean());
        await call("DELETE", photoPath + "/" + third.GetProperty("imageId").GetInt32(), null, 200, null);
        detail = await call("GET", $"products/{id}", null, 200, null);
        Assert.DoesNotContain(detail.GetProperty("images").EnumerateArray(), i => i.GetProperty("isPrimary").GetBoolean());
        Assert.True(File.Exists(Path.Combine(root, url.TrimStart('/'))));
        foreach (var (format, ext) in new[] { (SkiaSharp.SKEncodedImageFormat.Jpeg, "jpg"), (SkiaSharp.SKEncodedImageFormat.Webp, "webp") })
        {
            using var bytes = img.Encode(format, 90);
            var photo = await call("POST", photoPath, Form(bytes.ToArray(), filename: "test." + ext), 201, null);
            Assert.EndsWith("." + ext, photo.GetProperty("imageUrl").GetString());
        }
    }
}
