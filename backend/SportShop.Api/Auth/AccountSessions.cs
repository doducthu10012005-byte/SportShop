using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;

namespace SportShop.Api.Auth;

public sealed class AccountSessions(SportShopDbContext db, IPasswordHasher<User> passwords, SessionTokens tokens,
    PartitionedRateLimiter<string> loginAttempts)
{
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public async Task<User> RegisterAsync(RegisterRequest input, CancellationToken ct)
    {
        var name = input.FullName.Trim();
        if (name.Length < 2) throw new AuthFailure(400, "VALIDATION_ERROR", "Họ tên quá ngắn.", "fullName");
        var now = DateTime.UtcNow;
        var user = new User
        {
            RoleId = await db.Roles.Where(r => r.RoleName == "Customer").Select(r => r.RoleId).SingleAsync(ct),
            FullName = name, Email = NormalizeEmail(input.Email), PhoneNumber = input.PhoneNumber,
            CreatedAt = now, UpdatedAt = now, Cart = new Cart { CreatedAt = now, UpdatedAt = now }
        };
        user.PasswordHash = passwords.HashPassword(user, input.Password);
        db.Users.Add(user);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 } sql &&
            sql.Message.Contains("UQ_Users_Email", StringComparison.Ordinal))
        {
            throw new AuthFailure(409, "EMAIL_ALREADY_EXISTS", "Email đã được đăng ký.");
        }
        return user;
    }

    // All session writers lock the account before modifying its refresh tokens.
    private IQueryable<User> ByEmail(string email) => db.Users.FromSqlInterpolated(
        $"SELECT * FROM dbo.Users WITH (UPDLOCK) WHERE Email = {email}").Include(u => u.Role);
    internal static IQueryable<User> LockById(SportShopDbContext db, int id) => db.Users.FromSqlInterpolated(
        $"SELECT * FROM dbo.Users WITH (UPDLOCK) WHERE UserId = {id}").Include(u => u.Role);

    internal async Task<IssuedSession> LoginAsync(LoginRequest input, CancellationToken ct)
    {
        var email = NormalizeEmail(input.Email);
        using var lease = loginAttempts.AttemptAcquire(email);
        if (!lease.IsAcquired) throw new AuthFailure(429, "TOO_MANY_REQUESTS", "Vui lòng thử đăng nhập lại sau.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await ByEmail(email).SingleOrDefaultAsync(ct);
        if (user is null || passwords.VerifyHashedPassword(user, user.PasswordHash, input.Password) == PasswordVerificationResult.Failed)
            throw AuthFailure.InvalidCredentials();
        if (!user.IsActive) throw new AuthFailure(403, "ACCOUNT_LOCKED", "Tài khoản đã bị khóa.");
        var now = DateTime.UtcNow;
        user.LastLoginAt = user.UpdatedAt = now;
        var session = tokens.Issue(user, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return session;
    }

    internal async Task<IssuedSession> RefreshAsync(string? cookie, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cookie)) throw AuthFailure.InvalidSession();
        var hash = SessionTokens.Hash(cookie);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var existing = db.RefreshTokens.Where(t => t.TokenHash == hash);
        var id = await existing.Select(t => (int?)t.UserId).SingleOrDefaultAsync(ct);
        if (id is null) throw AuthFailure.InvalidSession();
        var user = await LockById(db, id.Value).SingleOrDefaultAsync(ct);
        if (user is null || !user.IsActive) throw AuthFailure.InvalidSession();
        var now = DateTime.UtcNow;
        var claimed = await existing.Where(t => t.RevokedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        if (claimed != 1) throw AuthFailure.InvalidSession();
        var session = tokens.Issue(user, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return session;
    }

    public async Task LogoutAsync(int id, string? cookie, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cookie)) return;
        var hash = SessionTokens.Hash(cookie);
        var now = DateTime.UtcNow;
        await db.RefreshTokens.Where(t => t.UserId == id && t.TokenHash == hash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    public async Task ChangeAsync(int id, int version, ChangePasswordRequest input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockById(db, id).SingleOrDefaultAsync(ct) ?? throw AuthFailure.InvalidSession();
        if (user.TokenVersion != version) throw new AuthFailure(401, "TOKEN_REVOKED", "Phiên đăng nhập đã bị thu hồi.");
        if (passwords.VerifyHashedPassword(user, user.PasswordHash, input.CurrentPassword) == PasswordVerificationResult.Failed)
            throw new AuthFailure(400, "VALIDATION_ERROR", "Mật khẩu hiện tại không đúng.", "currentPassword");
        if (user.MustChangePassword && input.CurrentPassword == input.NewPassword)
            throw new AuthFailure(400, "VALIDATION_ERROR", "Mật khẩu mới phải khác mật khẩu hiện tại.", "newPassword");
        await ReplacePasswordAsync(user, input.NewPassword, false, ct);
        await tx.CommitAsync(ct);
    }

    internal async Task<bool> ResetAdminAsync(string email, string password, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await ByEmail(NormalizeEmail(email)).SingleOrDefaultAsync(ct);
        if (user?.Role.RoleName != "Admin") return false;
        await ReplacePasswordAsync(user, password, true, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task ReplacePasswordAsync(User user, string password, bool forceChange, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        user.PasswordHash = passwords.HashPassword(user, password);
        user.UpdatedAt = now;
        user.MustChangePassword = forceChange;
        user.TokenVersion = checked(user.TokenVersion + 1);
        await db.RefreshTokens.Where(t => t.UserId == user.UserId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        await db.SaveChangesAsync(ct);
    }
}
