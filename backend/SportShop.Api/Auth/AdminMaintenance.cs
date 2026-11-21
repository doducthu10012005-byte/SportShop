using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;

namespace SportShop.Api.Auth;

internal static class AdminMaintenance
{
    public static async Task BootstrapAsync(IServiceProvider services, IConfiguration config)
    {
        if (!config.GetValue<bool>("AdminBootstrap:Enabled")) return;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportShopDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource='SportShop.InitialAdmin', @LockMode='Exclusive',
                @LockOwner='Transaction', @LockTimeout=15000;
            IF @result < 0 THROW 50001, 'Cannot acquire initialization lock.', 1;
            """);
        if (await db.Users.AnyAsync(u => u.Role.RoleName == "Admin")) return;
        var email = AccountSessions.NormalizeEmail(config["AdminBootstrap:Email"] ?? "");
        var password = config["AdminBootstrap:Password"] ?? "";
        if (email.Length is 0 or > 256 || !new EmailAddressAttribute().IsValid(email) || !ValidPassword(password))
            throw new InvalidOperationException("Invalid AdminBootstrap credentials.");
        var now = DateTime.UtcNow;
        var user = new User { Role = await db.Roles.SingleAsync(r => r.RoleName == "Admin"), Email = email,
            FullName = "Admin", MustChangePassword = true, CreatedAt = now, UpdatedAt = now };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("AdminMaintenance")
            .LogInformation("Initialized first admin {Email} at {UtcTime}", email, now);
    }

    public static async Task<int> ResetInteractiveAsync(IServiceProvider services)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return 1;
        Console.Write("Email Admin: ");
        var email = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(email)) return 1;
        var password = ReadHidden("Mật khẩu mới: ");
        if (!ValidPassword(password) || password != ReadHidden("Nhập lại mật khẩu: ")) return 1;
        Console.Write("Đặt lại mật khẩu và thu hồi mọi phiên? [y/N] ");
        if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase)) return 1;
        using var scope = services.CreateScope();
        if (!await scope.ServiceProvider.GetRequiredService<AccountSessions>().ResetAdminAsync(email, password, default))
            return 1;
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("AdminMaintenance")
            .LogInformation("Admin password reset for {Email} at {UtcTime} by {Operator}", email, DateTime.UtcNow, Environment.UserName);
        Console.WriteLine("Đã đặt lại mật khẩu.");
        return 0;
    }

    private static bool ValidPassword(string password) => password.Length >= 12 && new StrongPasswordAttribute().IsValid(password);

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace && buffer.Length > 0) buffer.Length--;
            else if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
        Console.WriteLine();
        return buffer.ToString();
    }
}
