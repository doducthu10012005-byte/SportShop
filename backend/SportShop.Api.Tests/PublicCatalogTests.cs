using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Net;
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
using SportShop.Api.PublicCatalog;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;
using Xunit;

namespace SportShop.Api.Tests;

public sealed class PublicCatalogTests
{
    [Theory]
    [InlineData(0, 50, "price", true)]
    [InlineData(50, 50, "-price", true)]
    [InlineData(51, 50, "newest", false)]
    [InlineData(-1, 50, "name", false)]
    [InlineData(0, 50, "createdAt", false)]
    public void ValidatesPriceRangeAndSort(int min, int max, string sort, bool valid)
    {
        var query = new CatalogQuery { MinPrice = min, MaxPrice = max, Sort = sort };
        Assert.Equal(valid, Validator.TryValidateObject(query, new ValidationContext(query), [], true));
    }

    private sealed class QueryCount : DbCommandInterceptor
    {
        public int Reads { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            Reads++;
            return ValueTask.FromResult(result);
        }
    }

    [SqlDevelopmentFact]
    public Task AnonymousCatalogOnSqlServer() => TemporarySqlDatabase.Run(async (connection, options) =>
    {
        await using var db = new SportShopDbContext(options);
        var fixture = await Seed(db);
        var commands = new QueryCount();
        using var root = new WebApplicationFactory<Program>();
        using var factory = root.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(log => log.ClearProviders());
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connection, ["AdminBootstrap:Enabled"] = "false",
                ["Payment:ExpiryJob:Enabled"] = "false",
                ["Jwt:SecretKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            }));
            builder.ConfigureServices(s => s.AddDbContext<SportShopDbContext>(o => o.AddInterceptors(commands)));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false
        });

        async Task<JsonElement> Get(string path, HttpStatusCode status = HttpStatusCode.OK)
        {
            using var response = await client.GetAsync("/api/v1/" + path);
            Assert.Equal(status, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(status == HttpStatusCode.OK, json.GetProperty("success").GetBoolean());
            if (status == HttpStatusCode.OK) return json.GetProperty("data");
            Assert.False(string.IsNullOrEmpty(json.GetProperty("traceId").GetString()));
            return json.GetProperty("errorCode");
        }
        async Task<JsonElement[]> Items(string query) =>
            (await Get("products?" + query)).GetProperty("items").EnumerateArray().ToArray();

        var (shoe, empty, hidden, noSale, cat, brand, otherBrand, medium, large, red, blue) = fixture;
        var prefix = $"categoryId={cat}&keyword=GIÀY&brandId={otherBrand}&brandId={brand}";
        Assert.Empty(await Items($"{prefix}&sizeId={medium}&colorId={blue}"));
        Assert.Empty(await Items($"{prefix}&sizeId={medium}&colorId={red}&inStock=true"));
        Assert.Empty(await Items($"{prefix}&sizeId={large}&colorId={blue}&maxPrice=100"));
        Assert.Empty(await Items($"brandId={otherBrand}&keyword=GIÀY"));
        Assert.Empty(await Items($"keyword=giay"));
        var query = $"{prefix}&sizeId={medium}&sizeId={large}&colorId={red}&colorId={blue}&minPrice=300&maxPrice=300&inStock=true";
        var card = Assert.Single(await Items(query));
        Assert.Equal(shoe, card.GetProperty("productId").GetInt32());
        Assert.Equal(100m, card.GetProperty("minPrice").GetDecimal());
        Assert.Equal(300m, card.GetProperty("maxPrice").GetDecimal());
        var cardColors = card.GetProperty("colors").EnumerateArray().Select(c => c.GetProperty("colorId").GetInt32()).Order();
        Assert.Equal(new[] { red, blue }.Order(), cardColors);
        Assert.Equal("/uploads/products/test.jpg", card.GetProperty("primaryImageUrl").GetString());
        var soldOutMatch = Assert.Single(await Items($"{prefix}&sizeId={medium}&colorId={red}&maxPrice=100"));
        Assert.True(soldOutMatch.GetProperty("inStock").GetBoolean());

        var before = commands.Reads;
        var all = await Get("products");
        Assert.Equal(3, commands.Reads - before); // count, page, colors for the entire page
        Assert.Equal(2, all.GetProperty("pagination").GetProperty("totalItems").GetInt32());
        Assert.Equal(12, all.GetProperty("pagination").GetProperty("pageSize").GetInt32());
        Assert.Equal(empty, all.GetProperty("items")[0].GetProperty("productId").GetInt32());
        Assert.False(all.GetProperty("items")[0].GetProperty("inStock").GetBoolean());
        Assert.Equal(200m, all.GetProperty("items")[0].GetProperty("maxPrice").GetDecimal());
        Assert.Equal(JsonValueKind.Null, all.GetProperty("items")[0].GetProperty("primaryImageUrl").ValueKind);
        Assert.Equal(shoe, Assert.Single(await Items("sort=price&pageSize=1")).GetProperty("productId").GetInt32());
        Assert.Equal(empty, Assert.Single(await Items("sort=price&pageSize=1&page=2")).GetProperty("productId").GetInt32());
        Assert.Equal(empty, (await Items("sort=-price"))[0].GetProperty("productId").GetInt32());
        Assert.Equal(empty, (await Items("sort=name"))[0].GetProperty("productId").GetInt32());
        Assert.Empty(await Items("page=2147483647&pageSize=50"));
        foreach (var invalid in new[] { "pageSize=51", "page=0", "sort=invalid", "minPrice=200&maxPrice=100", "colorId=-1", "sizeId=abc" })
            Assert.Equal("VALIDATION_ERROR", (await Get("products?" + invalid, HttpStatusCode.BadRequest)).GetString());

        var detail = await Get("products/" + shoe);
        var variants = detail.GetProperty("variants").EnumerateArray().ToList();
        Assert.Equal(3, variants.Count);
        Assert.Equal(0, variants.Single(v => v.GetProperty("sku").GetString() == "RED-M").GetProperty("quantityAvailable").GetInt32());
        Assert.Equal(5, variants.Single(v => v.GetProperty("sku").GetString() == "BLUE-L").GetProperty("quantityAvailable").GetInt32());
        Assert.Equal(2, detail.GetProperty("images").GetArrayLength());
        foreach (var id in new[] { hidden, noSale, int.MaxValue })
            Assert.Equal("NOT_FOUND", (await Get("products/" + id, HttpStatusCode.NotFound)).GetString());

        var parent = Assert.Single((await Get("categories")).EnumerateArray());
        Assert.Equal(cat, parent.GetProperty("categoryId").GetInt32());
        Assert.Single(parent.GetProperty("children")[0].GetProperty("children").EnumerateArray());
        Assert.Equal(2, (await Get("brands")).GetArrayLength());
        var sizes = (await Get("sizes")).EnumerateArray().Select(s => s.GetProperty("sizeId").GetInt32());
        Assert.Equal(new[] { medium, large }, sizes);
        Assert.Equal(3, (await Get("colors")).GetArrayLength());
        var methods = (await Get("payment-methods")).EnumerateArray().Select(m => m.GetProperty("methodCode").GetString());
        Assert.Equal(new[] { "COD", "BANK_TRANSFER" }, methods);
    });

    private sealed record Fixture(int Shoe, int Empty, int Hidden, int NoSale, int Category, int Brand, int OtherBrand,
        int Medium, int Large, int Red, int Blue);

    private static async Task<Fixture> Seed(SportShopDbContext db)
    {
        var now = DateTime.UtcNow;
        Category Category(string name, Category? parent = null) =>
            new() { CategoryName = name, Slug = name, ParentCategory = parent, CreatedAt = now, UpdatedAt = now };
        var root = Category("root");
        var leaf = Category("leaf", Category("child", root));
        var brand = new Brand { BrandName = "Alpha", Slug = "alpha" };
        var other = new Brand { BrandName = "Beta", Slug = "beta" };
        var large = new Size { SizeName = "L", SortOrder = 2 };
        var medium = new Size { SizeName = "M", SortOrder = 1 };
        var red = new Color { ColorName = "Red", HexCode = "#FF0000" };
        var blue = new Color { ColorName = "Blue", HexCode = "#0000FF" };
        var green = new Color { ColorName = "Green" };
        var inactiveCategory = Category("hidden");
        inactiveCategory.IsActive = false;
        var inactiveBrand = new Brand { BrandName = "Hidden", Slug = "hidden", IsActive = false };
        db.AddRange(inactiveCategory, inactiveBrand, new Size { SizeName = "inactive", IsActive = false },
            new Color { ColorName = "inactive", IsActive = false });
        Product Product(string name, string slug, Brand productBrand, int age) =>
            new() { ProductName = name, Slug = slug, Brand = productBrand, Category = leaf,
                CreatedAt = now.AddDays(-age), UpdatedAt = now };
        void Add(Product product, Color color, Size size, decimal price, int quantity, string sku, bool active = true) =>
            product.Variants.Add(new ProductVariant { Color = color, Size = size, Sku = sku, IsActive = active,
                Price = price, Inventory = new Inventory { QuantityOnHand = quantity } });

        var shoe = Product("Giày chạy", "shoe", brand, 2);
        Add(shoe, red, medium, 100, 0, "RED-M");
        Add(shoe, blue, large, 300, 5, "BLUE-L");
        Add(shoe, blue, medium, 180, 0, "BLUE-M", false);
        Add(shoe, red, large, 150, 0, "RED-L");
        Add(shoe, green, medium, 1, 99, "INACTIVE", false);
        shoe.Images.Add(new ProductImage { ImageUrl = "/uploads/products/test.jpg", IsPrimary = true });
        shoe.Images.Add(new ProductImage { Color = blue, ImageUrl = "/uploads/products/blue.jpg" });
        var empty = Product("Áo thun", "shirt", other, 1);
        Add(empty, blue, medium, 200, 0, "SHIRT");
        Add(empty, green, medium, 900, 10, "HIDDEN-STOCK", false);
        var hidden = Product("Hidden", "hidden-product", brand, 0);
        hidden.IsActive = false;
        Add(hidden, blue, large, 1, 9, "HIDDEN");
        var noSale = Product("No variants on sale", "no-sale", brand, 0);
        Add(noSale, blue, medium, 1, 99, "OFF", false);
        var hiddenByBrand = Product("Hidden brand", "hidden-brand", inactiveBrand, 0);
        Add(hiddenByBrand, red, medium, 1, 2, "OFF-BRAND");
        var hiddenByCategory = Product("Hidden category", "hidden-category", brand, 0);
        hiddenByCategory.Category = inactiveCategory;
        Add(hiddenByCategory, red, medium, 1, 2, "OFF-CATEGORY");
        db.Products.AddRange(shoe, empty, hidden, noSale, hiddenByBrand, hiddenByCategory);
        await db.SaveChangesAsync();
        return new(shoe.ProductId, empty.ProductId, hidden.ProductId, noSale.ProductId, root.CategoryId, brand.BrandId,
            other.BrandId, medium.SizeId, large.SizeId, red.ColorId, blue.ColorId);
    }
}
