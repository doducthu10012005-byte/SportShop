using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SportShop.Api.Auth;
using SportShop.Api.CustomerOrders;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;
using Xunit;

namespace SportShop.Api.Tests;

public sealed partial class CustomerOrderTests
{
    private sealed record Actor(int CartId, string Token);
    private sealed record Reply(int Status, JsonElement Body)
    {
        public JsonElement Data => Body.GetProperty("data");
        public void Error(int status, string code)
        {
            Assert.Equal(status, Status);
            Assert.Equal(code, Body.GetProperty("errorCode").GetString());
            Assert.False(string.IsNullOrWhiteSpace(Body.GetProperty("traceId").GetString()));
        }
    }

    // Synchronize requests at a particular SQL statement, on separate request connections.
    private sealed class Gate : DbCommandInterceptor
    {
        private string match = "";
        private int remaining;
        private TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailCartDelete { get; set; }
        public bool FailPaymentUpdate { get; set; }
        public int ConflictsToSimulate { get; set; }
        public DateTime? ConfirmationClock { get; set; }
        public DateTime? ExpiryClock { get; set; }
        private bool confirmationUpdateReached;

        public void Arm(string sql, int count)
        {
            arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            match = sql;
            Volatile.Write(ref remaining, count);
        }

        public Task Wait() => arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
        public void Release() => release.TrySetResult();
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (FailCartDelete && command.CommandText.Contains("DELETE", StringComparison.Ordinal) &&
                command.CommandText.Contains("[CartItems]", StringComparison.Ordinal))
                throw new InvalidOperationException("Test failure at cart deletion.");
            if (FailPaymentUpdate && command.CommandText.Contains("UPDATE dbo.Payments", StringComparison.Ordinal))
                throw new InvalidOperationException("Test failure during payment update.");
            if (ConflictsToSimulate > 0 && (command.CommandText.Contains("UPDATE dbo.Orders SET StatusId", StringComparison.Ordinal) ||
                command.CommandText.Contains(ClaimForPayment, StringComparison.Ordinal)))
            {
                ConflictsToSimulate--;
                return InterceptionResult<int>.SuppressWithResult(0);
            }
            // Pin the clock in the conditional UPDATE to exercise an exact expiry boundary.
            if (ConfirmationClock is DateTime now &&
                command.CommandText.Contains("SET PaymentStatus='Paid', ReceivedAmount=", StringComparison.Ordinal) &&
                command.CommandText.Contains("TransactionCode=", StringComparison.Ordinal))
            {
                confirmationUpdateReached = true;
                SetClock(command, now);
            }
            if (ExpiryClock is DateTime expiry &&
                command.CommandText.Contains("SET PaymentStatus='Expired'", StringComparison.Ordinal))
                SetClock(command, expiry);
            await Rendezvous(command, ct);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (confirmationUpdateReached && ConfirmationClock is DateTime now &&
                command.CommandText.Contains("SELECT SYSUTCDATETIME() AS [Value]", StringComparison.Ordinal))
                SetClock(command, now);
            if (ExpiryClock is DateTime expiry &&
                command.CommandText.Contains("SELECT SYSUTCDATETIME() AS [Value]", StringComparison.Ordinal))
                SetClock(command, expiry);
            await Rendezvous(command, ct);
            return result;
        }

        private static void SetClock(DbCommand command, DateTime now)
        {
            var clock = command.CreateParameter();
            clock.ParameterName = "@testNow";
            clock.DbType = System.Data.DbType.DateTime2;
            clock.Value = now;
            command.Parameters.Add(clock);
            command.CommandText = command.CommandText.Replace("SYSUTCDATETIME()", "@testNow", StringComparison.Ordinal);
        }

        private async Task Rendezvous(DbCommand command, CancellationToken ct)
        {
            if (Volatile.Read(ref remaining) > 0 && command.CommandText.Contains(match, StringComparison.Ordinal))
            {
                if (Interlocked.Decrement(ref remaining) == 0) arrived.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            }
        }
    }

    private sealed class Shop(HttpClient http, SessionTokens tokens, SportShopDbContext db, IConfiguration config, Gate gate,
        IServiceProvider services)
    {
        public SportShopDbContext Db => db;
        public IConfiguration Config => config;
        public Gate Gate => gate;
        public Task Sweep() => services.GetRequiredService<BankTransferExpirySweep>().Run(CancellationToken.None);

        public async Task ExpireCandidate(int orderId, int paymentId)
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OrderTransitions>().ExpireTransfer(orderId, paymentId, CancellationToken.None);
        }
        public async Task<Actor> Account(string name, string role = "Customer")
        {
            var user = new User { Email = name + "@example.test", Role = await db.Roles.SingleAsync(r => r.RoleName == role),
                Cart = role == "Customer" ? new() : null };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return new(user.Cart?.CartId ?? 0, tokens.Issue(user, DateTime.UtcNow).Body.AccessToken);
        }

        public async Task<ProductVariant> Variant(string name, decimal price, int stock)
        {
            var variant = new ProductVariant { Sku = name, Price = price, Size = new() { SizeName = name },
                Color = new() { ColorName = name }, Inventory = new() { QuantityOnHand = stock },
                Product = new() { ProductName = name, Slug = name, Brand = new() { BrandName = name, Slug = name },
                    Category = new() { CategoryName = name, Slug = name } } };
            variant.Product.Images.Add(new() { ImageUrl = "/primary-" + name, IsPrimary = true });
            variant.Product.Images.Add(new() { ImageUrl = "/color-" + name, Color = variant.Color });
            db.ProductVariants.Add(variant);
            await db.SaveChangesAsync();
            return variant;
        }

        public async Task Fill(Actor a, ProductVariant v, int qty = 1)
        {
            db.CartItems.Add(new() { CartId = a.CartId, VariantId = v.VariantId, Quantity = qty });
            await db.SaveChangesAsync();
        }

        public Task<Reply> Send(Actor? a, HttpMethod method, string suffix = "", object? input = null) =>
            SendTo(a, method, "/api/v1/orders" + suffix, input);

        public async Task<Reply> SendTo(Actor? a, HttpMethod method, string path, object? input = null)
        {
            using var req = new HttpRequestMessage(method, path);
            if (a != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", a.Token);
            if (input != null) req.Content = JsonContent.Create(input);
            using var response = await http.SendAsync(req);
            return new((int)response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
        }

        public Task<Reply> Place(Actor? a, int method = 1) => Send(a, HttpMethod.Post, input: new {
            recipientName = "Khách", recipientPhone = "0912345678", shippingAddress = "Hà Nội",
            paymentMethodId = method, subtotal = 1, shippingFee = 1, totalAmount = 2 });

        public async Task AssertNoCheckout(int cartItems, int stock)
        {
            Assert.Equal(0, await db.Orders.CountAsync());
            Assert.Equal(0, await db.OrderItems.CountAsync());
            Assert.Equal(0, await db.OrderStatusHistories.CountAsync());
            Assert.Equal(0, await db.Payments.CountAsync());
            Assert.Equal(0, await db.InventoryTransactions.CountAsync());
            Assert.Equal(cartItems, await db.CartItems.CountAsync());
            Assert.Equal(stock, await db.Inventories.SumAsync(i => i.QuantityOnHand));
        }
    }

    private static Task Run(Func<Shop, Task> test) => TemporarySqlDatabase.Run(async (connection, options) =>
    {
        var gate = new Gate();
        using var root = new WebApplicationFactory<Program>();
        using var factory = root.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(l => l.ClearProviders());
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> {
                ["ConnectionStrings:DefaultConnection"] = connection, ["AdminBootstrap:Enabled"] = "false",
                ["Payment:ExpiryJob:Enabled"] = "false",
                ["Jwt:SecretKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
                ["Payment:BankName"] = "", ["Payment:BankAccountNumber"] = "" }));
            builder.ConfigureServices(s => s.AddDbContext<SportShopDbContext>(o => o.AddInterceptors(gate)));
        });
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var scope = factory.Services.CreateScope();
        await using var db = new SportShopDbContext(options);
        await test(new Shop(http, scope.ServiceProvider.GetRequiredService<SessionTokens>(), db,
            factory.Services.GetRequiredService<IConfiguration>(), gate, factory.Services));
    });

    [SqlDevelopmentFact]
    public Task TransferConfigurationAndPaymentValidation() => Run(async s =>
    {
        var a = await s.Account("customer");
        var v = await s.Variant("red", 499999.95m, 4);
        await s.Fill(a, v, 2);
        foreach (var account in new[] { "", "123x" })
        {
            s.Config["Payment:BankName"] = "Test Bank";
            s.Config["Payment:BankAccountNumber"] = account;
            var failed = await s.Place(a, 2);
            failed.Error(500, "INTERNAL_ERROR");
            Assert.DoesNotContain("Bank", failed.Body.GetRawText());
            await s.AssertNoCheckout(1, 4);
        }
        await s.Db.PaymentMethods.Where(m => m.PaymentMethodId == 2).ExecuteUpdateAsync(u => u.SetProperty(m => m.IsActive, false));
        (await s.Place(a, 2)).Error(400, "VALIDATION_ERROR");
        await s.AssertNoCheckout(1, 4);
        await s.Db.PaymentMethods.Where(m => m.PaymentMethodId == 2).ExecuteUpdateAsync(u => u.SetProperty(m => m.IsActive, true));
        s.Config["Payment:BankAccountNumber"] = "00001234";
        s.Config["Payment:BankTransferTimeoutMinutes"] = "37";
        var start = await s.Db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync();
        var result = await s.Place(a, 2);
        Assert.Equal(201, result.Status);
        var data = result.Data;
        Assert.Equal(30000m, data.GetProperty("shippingFee").GetDecimal());
        var instructions = data.GetProperty("payment").GetProperty("instructions").GetString()!;
        foreach (var value in new[] { "00001234", "Test Bank", "1.029.999,9", data.GetProperty("orderCode").GetString()! })
            Assert.Contains(value, instructions);
        var pay = await s.Db.Payments.AsNoTracking().SingleAsync();
        var end = await s.Db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync();
        Assert.InRange(pay.CreatedAt, start, end);
        Assert.Equal(pay.CreatedAt.AddMinutes(37), pay.ExpiresAt);
        Assert.Equal(1029999.90m, pay.Amount);
        Assert.Equal("Pending", pay.PaymentStatus);
        Assert.Null(pay.ReceivedAmount);
        Assert.Equal(pay.ExpiresAt, data.GetProperty("payment").GetProperty("expiresAt").GetDateTime().ToUniversalTime());
    });

    [SqlDevelopmentFact]
    public Task CheckoutSnapshotsOwnershipAndOrderHistory() => Run(async s =>
    {
        var a = await s.Account("customer");
        var other = await s.Account("other");
        var admin = await s.Account("admin", "Admin");
        foreach (var (actor, status, code) in new[] { ((Actor?)null, 401, "UNAUTHORIZED"), (admin, 403, "FORBIDDEN") })
        {
            (await s.Place(actor)).Error(status, code);
            (await s.Send(actor, HttpMethod.Get)).Error(status, code);
            (await s.Send(actor, HttpMethod.Get, "/1")).Error(status, code);
        }
        (await s.Place(a)).Error(400, "CART_EMPTY");
        var v = await s.Variant("shoes", 500000, 4);
        await s.Fill(a, v, 2);
        var first = await s.Place(a);
        Assert.Equal(201, first.Status);
        var id = first.Data.GetProperty("orderId").GetInt32();
        var path = "/" + id;
        Assert.Equal(0, first.Data.GetProperty("shippingFee").GetDecimal());
        Assert.Equal(1000000, first.Data.GetProperty("totalAmount").GetDecimal());
        var payment = first.Data.GetProperty("payment");
        Assert.Equal(JsonValueKind.Null, payment.GetProperty("expiresAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, payment.GetProperty("instructions").ValueKind);
        var sale = await s.Db.InventoryTransactions.SingleAsync();
        Assert.Equal("Sale", sale.TransactionType);
        Assert.Equal(-2, sale.QuantityChange);
        Assert.Equal(2, sale.QuantityAfter);
        Assert.Equal((await s.Db.OrderItems.SingleAsync()).OrderItemId, sale.OrderItemId);
        Assert.False(await s.Db.CartItems.AnyAsync());
        v.Price = 1;
        v.Product.ProductName = "new";
        v.Sku = "changed";
        v.Color.ColorName = "changed";
        v.Size.SizeName = "changed";
        s.Db.RemoveRange(v.Product.Images);
        await s.Db.SaveChangesAsync();
        var detail = await s.Send(a, HttpMethod.Get, path);
        Assert.Equal(200, detail.Status);
        var item = Assert.Single(detail.Data.GetProperty("items").EnumerateArray());
        foreach (var field in new[] { "sku", "colorName", "sizeName", "productName" })
            Assert.Equal("shoes", item.GetProperty(field).GetString());
        Assert.Equal("/color-shoes", item.GetProperty("imageUrl").GetString());
        Assert.Equal(500000m, item.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(1000000m, item.GetProperty("lineTotal").GetDecimal());
        Assert.False(detail.Data.GetProperty("payment").TryGetProperty("transactionCode", out _));
        Assert.Equal("PENDING", Assert.Single(detail.Data.GetProperty("statusHistory").EnumerateArray()).GetProperty("statusCode").GetString());
        (await s.Send(other, HttpMethod.Get, path)).Error(404, "NOT_FOUND");
        Assert.Empty((await s.Send(other, HttpMethod.Get)).Data.GetProperty("items").EnumerateArray());
        await s.Fill(a, v);
        Assert.Equal(201, (await s.Place(a)).Status);
        var list = (await s.Send(a, HttpMethod.Get, "?pageSize=1&sort=createdAt&statusCode=PENDING")).Data;
        Assert.Equal(2, list.GetProperty("pagination").GetProperty("totalItems").GetInt32());
        var summary = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(2, summary.GetProperty("totalItems").GetInt32());
        Assert.Equal(id, summary.GetProperty("orderId").GetInt32());
        Assert.Equal(id, (await s.Send(a, HttpMethod.Get, "?pageSize=1&page=2")).Data.GetProperty("items")[0].GetProperty("orderId").GetInt32());
        Assert.Empty((await s.Send(a, HttpMethod.Get, "?statusCode=CANCELLED")).Data.GetProperty("items").EnumerateArray());
        (await s.Send(a, HttpMethod.Get, "?sort=bogus")).Error(400, "VALIDATION_ERROR");
    });

    [SqlDevelopmentFact]
    public Task InvalidLinesRollbackEveryWrite() => Run(async s =>
    {
        var a = await s.Account("buyer");
        var first = await s.Variant("first", 2, 3);
        var last = await s.Variant("last", 3, 0);
        await s.Fill(a, first);
        await s.Fill(a, last);
        foreach (var hideProduct in new[] { true, false })
        {
            last.IsActive = hideProduct;
            last.Product.IsActive = !hideProduct;
            await s.Db.SaveChangesAsync();
            (await s.Place(a)).Error(400, "VALIDATION_ERROR");
            await s.AssertNoCheckout(2, 3);
        }
        last.IsActive = last.Product.IsActive = true;
        await s.Db.SaveChangesAsync();
        var failed = await s.Place(a);
        failed.Error(409, "INSUFFICIENT_STOCK");
        Assert.Equal("variantId:" + last.VariantId, failed.Body.GetProperty("errors")[0].GetProperty("field").GetString());
        await s.AssertNoCheckout(2, 3);
        Assert.Equal(3, await s.Db.Inventories.Where(i => i.VariantId == first.VariantId).Select(i => i.QuantityOnHand).SingleAsync());
    });

    [SqlDevelopmentFact]
    public Task SimultaneousCheckoutsSerializeCartsInventoryAndCodes() => Run(async s =>
    {
        var a = await s.Account("buyerA");
        var b = await s.Account("buyerB");
        var v = await s.Variant("last", 100, 2);
        async Task<Reply[]> Race(Actor x, Actor y)
        {
            s.Gate.Arm("FROM [Carts] WITH (UPDLOCK", 2);
            var requests = Task.WhenAll(s.Place(x), s.Place(y));
            try { await s.Gate.Wait(); } finally { s.Gate.Release(); }
            return await requests;
        }
        await s.Fill(a, v);
        var same = await Race(a, a);
        Assert.Single(same, r => r.Status == 201);
        Assert.Single(same, r => r.Status == 400).Error(400, "CART_EMPTY");
        await s.Fill(a, v);
        await s.Fill(b, v);
        var last = await Race(a, b);
        Assert.Single(last, r => r.Status == 201);
        Assert.Single(last, r => r.Status == 409).Error(409, "INSUFFICIENT_STOCK");
        Assert.Equal(2, await s.Db.Orders.CountAsync());
        Assert.Equal(2, await s.Db.InventoryTransactions.CountAsync());
        Assert.Equal(0, await s.Db.Inventories.SumAsync(i => i.QuantityOnHand));
        Assert.Single(await s.Db.CartItems.ToListAsync());
        var existing = await s.Db.Orders.OrderBy(o => o.OrderId).FirstAsync();
        var prefix = existing.OrderCode[..11];
        Assert.Matches(@"^SP\d{8}-0001$", existing.OrderCode);
        var vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
        Assert.Equal("SP" + TimeZoneInfo.ConvertTimeFromUtc(existing.CreatedAt, vietnam).ToString("yyyyMMdd") + "-", prefix);
        existing.OrderCode = prefix + "9999";
        await s.Db.SaveChangesAsync();
        await s.Db.Inventories.ExecuteUpdateAsync(u => u.SetProperty(i => i.QuantityOnHand, 2));
        var extra = await s.Variant("second", 1, 2);
        await s.Db.CartItems.ExecuteDeleteAsync();
        await s.Fill(a, v);
        await s.Fill(b, extra);
        var both = await Race(a, b);
        Assert.All(both, r => Assert.Equal(201, r.Status));
        Assert.Equal(new[] { prefix + "10000", prefix + "10001" },
            both.Select(r => r.Data.GetProperty("orderCode").GetString()).Order());
    });

    [SqlDevelopmentFact]
    public Task CatalogChangeDuringCheckoutUsesCommittedCatalogState() => Run(async s =>
    {
        var a = await s.Account("buyer");
        var v = await s.Variant("item", 50, 3);
        await s.Fill(a, v);
        async Task<Reply> RaceEdit(bool active, decimal price)
        {
            await using var tx = await s.Db.Database.BeginTransactionAsync();
            await s.Db.Products.Where(p => p.ProductId == v.ProductId).ExecuteUpdateAsync(u => u.SetProperty(p => p.IsActive, active));
            await s.Db.ProductVariants.Where(p => p.VariantId == v.VariantId).ExecuteUpdateAsync(u => u.SetProperty(p => p.Price, price));
            s.Gate.Arm("FROM [CartItems]", 1);
            var request = s.Place(a);
            try { await s.Gate.Wait(); } finally { s.Gate.Release(); }
            await tx.CommitAsync();
            return await request;
        }
        (await RaceEdit(false, 100)).Error(400, "VALIDATION_ERROR");
        await s.AssertNoCheckout(1, 3);
        var placed = await RaceEdit(true, 200);
        Assert.Equal(201, placed.Status);
        Assert.Equal(200m, placed.Data.GetProperty("subtotal").GetDecimal());
        Assert.Equal(200m, await s.Db.OrderItems.Select(i => i.UnitPrice).SingleAsync());
        Assert.Equal(30200m, await s.Db.Payments.Select(p => p.Amount).SingleAsync());
    });

    [SqlDevelopmentFact]
    public Task CatalogSnapshotRemainsLockedUntilCommit() => Run(async s =>
    {
        var a = await s.Account("buyer");
        var v = await s.Variant("item", 50, 2);
        await s.Fill(a, v);
        s.Gate.Arm("FROM dbo.Inventories WITH (UPDLOCK", 1);
        var request = s.Place(a);
        try
        {
            await s.Gate.Wait();
            await using var writer = await s.Db.Database.BeginTransactionAsync();
            await s.Db.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 0");
            var blocked = await Record.ExceptionAsync(() => s.Db.ProductVariants.Where(p => p.VariantId == v.VariantId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Price, 90).SetProperty(p => p.IsActive, false)));
            Assert.Equal(1222, Assert.IsType<SqlException>(blocked?.GetBaseException()).Number);
            await writer.RollbackAsync();
        }
        finally { s.Gate.Release(); }
        Assert.Equal(201, (await request).Status);
        var changed = await s.Db.ProductVariants.Where(p => p.VariantId == v.VariantId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Price, 90).SetProperty(p => p.IsActive, false));
        Assert.Equal(1, changed);
        Assert.Equal(50m, await s.Db.OrderItems.Select(i => i.UnitPrice).SingleAsync());
        Assert.Equal(30050m, await s.Db.Payments.Select(p => p.Amount).SingleAsync());
    });

    [SqlDevelopmentFact]
    public Task LateFailureRollsBackOrderLedgerAndCartTimestamp() => Run(async s =>
    {
        var a = await s.Account("buyer");
        var v = await s.Variant("item", 50, 3);
        await s.Fill(a, v, 2);
        var original = await s.Db.Carts.Select(c => c.UpdatedAt).SingleAsync();
        s.Gate.FailCartDelete = true;
        (await s.Place(a)).Error(500, "INTERNAL_ERROR");
        await s.AssertNoCheckout(1, 3);
        Assert.Equal(2, await s.Db.CartItems.Select(i => i.Quantity).SingleAsync());
        Assert.Equal(original, await s.Db.Carts.Select(c => c.UpdatedAt).SingleAsync());
        s.Gate.FailCartDelete = false;
        Assert.Equal(201, (await s.Place(a)).Status);
    });

    [SqlDevelopmentFact]
    public Task OrderReadsChooseLatestPaymentAndUseSqlClockForOverdue() => Run(async s =>
    {
        var a = await s.Account("buyer");
        var v = await s.Variant("item", 50, 3);
        await s.Fill(a, v);
        Assert.Equal(201, (await s.Place(a)).Status);
        var first = await s.Db.Payments.SingleAsync();
        var now = await s.Db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync();
        first.PaymentStatus = "Failed";
        first.CreatedAt = now.AddDays(-2);
        await s.Db.SaveChangesAsync();
        var latest = new Payment { OrderId = first.OrderId, PaymentMethodId = 2, PaymentStatus = "Pending",
            Amount = first.Amount, CreatedAt = first.CreatedAt, ExpiresAt = now.AddDays(-1), UpdatedAt = now };
        s.Db.Payments.Add(latest);
        await s.Db.SaveChangesAsync();
        var payment = (await s.Send(a, HttpMethod.Get, "/" + first.OrderId)).Data.GetProperty("payment");
        Assert.Equal("BANK_TRANSFER", payment.GetProperty("methodCode").GetString());
        Assert.Equal("Pending", payment.GetProperty("paymentStatus").GetString());
        Assert.True(payment.GetProperty("isOverdue").GetBoolean());
        Assert.False(payment.TryGetProperty("transactionCode", out _));
        Assert.Equal("Pending", (await s.Send(a, HttpMethod.Get)).Data.GetProperty("items")[0].GetProperty("paymentStatus").GetString());
        first.CreatedAt = now;
        first.UpdatedAt = now;
        await s.Db.SaveChangesAsync();
        payment = (await s.Send(a, HttpMethod.Get, "/" + first.OrderId)).Data.GetProperty("payment");
        Assert.Equal("Failed", payment.GetProperty("paymentStatus").GetString());
        Assert.False(payment.GetProperty("isOverdue").GetBoolean());
    });
}
