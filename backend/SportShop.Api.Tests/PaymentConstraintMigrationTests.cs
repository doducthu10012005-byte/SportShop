using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;
using Xunit;

namespace SportShop.Api.Tests;

public sealed class PaymentConstraintMigrationTests
{
    private const string Initial = "20261120150911_InitialCreate";
    private const string Upgrade = "20261121115336_RequireReceivedAmountForCollectedPayments";
    private const string Constraint = "CK_Payments_Status_ReceivedAmount_PaidAt";
    private const decimal Amount = 100.25m;
    private static readonly DateTime Created = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Statuses = ["Paid", "RefundPending", "Refunded", "Pending", "Failed", "Expired"];

    private static bool Collected(string status) => status is "Paid" or "RefundPending" or "Refunded";

    private static async Task<int> Seed(SportShopDbContext db)
    {
        var user = new User { RoleId = 2, Email = "payment-check@example.invalid", CreatedAt = Created, UpdatedAt = Created };
        Order NewOrder(string code) => new()
        {
            OrderCode = code, User = user, StatusId = 1, RecipientName = "Test", RecipientPhone = "000",
            ShippingAddress = "Test", Subtotal = Amount, TotalAmount = Amount, CreatedAt = Created, UpdatedAt = Created
        };

        foreach (var status in Statuses)
        {
            var order = NewOrder(status);
            if (status is "RefundPending" or "Refunded" or "Failed" or "Expired")
            {
                order.StatusId = 5;
                order.CancelReason = "Test";
            }
            order.Payments.Add(new Payment
            {
                PaymentMethodId = 2, PaymentStatus = status, Amount = Amount, ReceivedAmount = Collected(status) ? Amount : null,
                TransactionCode = Collected(status) ? "TEST-" + status : null,
                PaidAt = Collected(status) ? Created.AddHours(1) : null, ExpiresAt = Created.AddDays(1),
                RefundedAt = status == "Refunded" ? Created.AddDays(2) : null,
                CreatedAt = Created, UpdatedAt = Created.AddDays(2)
            });
            db.Orders.Add(order);
        }
        var spare = NewOrder("probe");
        db.Orders.Add(spare);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return spare.OrderId;
    }

    private static Task<string> PaymentRows(SportShopDbContext db) => db.Database.SqlQueryRaw<string>("""
        SELECT (SELECT * FROM dbo.Payments ORDER BY PaymentId FOR JSON PATH, INCLUDE_NULL_VALUES) AS [Value]
        """).SingleAsync();

    // SingleAsync also verifies that the named constraint exists, is enabled and trusted.
    private static Task<string> TrustedDefinition(SportShopDbContext db) => db.Database.SqlQuery<string>($"""
        SELECT definition AS [Value] FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.Payments')
            AND name = {Constraint} AND is_disabled = 0 AND is_not_trusted = 0
        """).SingleAsync();

    [SqlDevelopmentFact]
    public Task UpgradeEnforcesPaymentStatesAndPreservesRows() => TemporarySqlDatabase.Run(async (_, options) =>
    {
        await using var db = new SportShopDbContext(options);
        var spareOrder = await Seed(db);
        var before = await PaymentRows(db);
        var originalDefinition = await TrustedDefinition(db);
        await db.Database.MigrateAsync(Upgrade);
        Assert.NotEqual(originalDefinition, await TrustedDefinition(db));
        Assert.Equal(new[] { Initial, Upgrade }, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(before, await PaymentRows(db));

        foreach (var status in Statuses)
        {
            var collected = Collected(status);
            var paidAt = collected ? Created.AddHours(1) : (DateTime?)null;
            foreach (var insert in new[] { true, false })
            {
                await Probe(db, spareOrder, status, insert, collected ? Amount : null, paidAt, valid: true);
                await Probe(db, spareOrder, status, insert, collected ? null : Amount, paidAt, valid: false);
                await Probe(db, spareOrder, status, insert, collected ? Amount : null,
                    collected ? null : Created.AddHours(1), valid: false);
                if (collected)
                    await Probe(db, spareOrder, status, insert, Amount - 0.01m, paidAt, valid: false);
            }
        }

        Assert.Equal(before, await PaymentRows(db));
        await db.Database.MigrateAsync(Initial);
        Assert.Equal(originalDefinition, await TrustedDefinition(db));
        Assert.Equal(before, await PaymentRows(db));
    }, Initial);

    private static async Task Probe(SportShopDbContext db, int spareOrder, string status, bool insert,
        decimal? received, DateTime? paidAt, bool valid)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        Task<int> Write() => insert
            ? db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT dbo.Payments (OrderId, PaymentMethodId, Amount, ReceivedAmount, PaymentStatus, ExpiresAt,
                    PaidAt, RefundedAt, CreatedAt, UpdatedAt)
                SELECT {spareOrder}, PaymentMethodId, Amount, {received}, PaymentStatus, ExpiresAt,
                    {paidAt}, RefundedAt, CreatedAt, UpdatedAt FROM dbo.Payments WHERE PaymentStatus = {status}
                """)
            : db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE dbo.Payments SET ReceivedAmount = {received}, PaidAt = {paidAt} WHERE PaymentStatus = {status}
                """);
        if (valid)
            Assert.Equal(1, await Write());
        else
        {
            var error = await Assert.ThrowsAsync<SqlException>(Write);
            Assert.Equal(547, error.Number);
            Assert.Contains(Constraint, error.Message);
        }
        await transaction.RollbackAsync();
    }

    [SqlDevelopmentFact]
    public Task InvalidHistoricalDataStopsUpgradeBeforeReplacement() => TemporarySqlDatabase.Run(async (_, options) =>
    {
        await using var db = new SportShopDbContext(options);
        await Seed(db);
        Assert.Equal(3, await db.Database.ExecuteSqlRawAsync("""
            UPDATE dbo.Payments SET ReceivedAmount = NULL WHERE PaymentStatus IN ('Paid', 'RefundPending', 'Refunded')
            """));
        var before = await PaymentRows(db);
        var originalDefinition = await TrustedDefinition(db);
        var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.MigrateAsync(Upgrade));
        Assert.Equal(51041, error.Number);
        Assert.Contains("Reconcile", error.Message);
        Assert.Equal(originalDefinition, await TrustedDefinition(db));
        Assert.Equal(new[] { Initial }, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(before, await PaymentRows(db));
    }, Initial);

    [SqlDevelopmentFact]
    public Task FailureDuringReplacementRollsBackDdlAndHistory() => TemporarySqlDatabase.Run(async (_, options) =>
    {
        await using var db = new SportShopDbContext(options);
        await Seed(db);
        var before = await PaymentRows(db);
        var originalDefinition = await TrustedDefinition(db);

        // Only in this disposable database: fail the ADD, after the DROP has executed.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER FailPaymentCheckAdd ON DATABASE FOR ALTER_TABLE AS
            BEGIN
                IF EVENTDATA().value('(/EVENT_INSTANCE/TSQLCommand/CommandText)[1]', 'nvarchar(max)')
                    LIKE '%WITH CHECK ADD CONSTRAINT%CK_Payments_Status_ReceivedAmount_PaidAt%'
                    THROW 51042, 'Injected payment constraint replacement failure.', 1;
            END
            """);
        var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.MigrateAsync(Upgrade));
        Assert.Contains(error.Errors.Cast<SqlError>(), e => e.Number == 51042);
        Assert.Equal(originalDefinition, await TrustedDefinition(db));
        Assert.Equal(new[] { Initial }, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(before, await PaymentRows(db));

        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailPaymentCheckAdd ON DATABASE");
        await db.Database.MigrateAsync(Upgrade);
        Assert.NotEqual(originalDefinition, await TrustedDefinition(db));
        Assert.Equal(before, await PaymentRows(db));
    }, Initial);
}
