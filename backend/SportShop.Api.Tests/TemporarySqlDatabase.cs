using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SportShop.Api.Data;

namespace SportShop.Api.Tests;

internal static class TemporarySqlDatabase
{
    public static async Task Run(Func<string, DbContextOptions<SportShopDbContext>, Task> verify,
        string? startingMigration = null)
    {
        var secrets = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
        var connection = new SqlConnectionStringBuilder(secrets.GetConnectionString("DefaultConnection"));
        if (connection.InitialCatalog != "SportShopDb" || !connection.IntegratedSecurity || connection.AttachDBFilename.Length > 0)
            throw new InvalidOperationException("Expected Development SportShopDb with Windows authentication.");
        var database = "SportShopTest_" + Guid.NewGuid().ToString("N");
        connection.InitialCatalog = "master";
        await using var master = new SqlConnection(connection.ConnectionString);
        await master.OpenAsync();
        async Task Sql(string statement)
        {
            await using var cmd = master.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync();
        }

        // CREATE must succeed before entering cleanup. Only this test owns this generated name.
        await Sql($"CREATE DATABASE [{database}] COLLATE Vietnamese_100_CI_AS");
        try
        {
            connection.InitialCatalog = database;
            connection.Pooling = false;
            var options = new DbContextOptionsBuilder<SportShopDbContext>().UseSqlServer(connection.ConnectionString).Options;
            await using (var db = new SportShopDbContext(options))
            {
                if (startingMigration == null) await db.Database.MigrateAsync();
                else await db.Database.MigrateAsync(startingMigration);
            }
            await verify(connection.ConnectionString, options);
        }
        finally
        {
            await Sql($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
        }
    }
}
