using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;
using SportShop.Api.Middleware;

namespace SportShop.Api.Auth;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProfileInput
{
    [Required] public string FullName { get; init; } = "";
    public string? PhoneNumber { get; init; }
    public DateOnly? DateOfBirth { get; init; }
    public string? Gender { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AddressInput
{
    [Required, MaxLength(100)] public string RecipientName { get; init; } = "";
    [Required] public string PhoneNumber { get; init; } = "";
    [Required, MaxLength(100)] public string Province { get; init; } = "";
    [Required, MaxLength(100)] public string District { get; init; } = "";
    [Required, MaxLength(100)] public string Ward { get; init; } = "";
    [Required, MaxLength(255)] public string AddressLine { get; init; } = "";
    public bool? IsDefault { get; init; }
}

public sealed class AddressQuery
{
    [Range(1, int.MaxValue)] public int Page { get; init; } = 1;
    [Range(1, 50)] public int PageSize { get; init; } = 12;
    [RegularExpression("-?(addressId|createdAt|isDefault)")] public string Sort { get; init; } = "addressId";
}

public sealed class CurrentUserData(SportShopDbContext db)
{
    private sealed record AddressView(int AddressId, string RecipientName, string PhoneNumber,
        string Province, string District, string Ward, string AddressLine, bool IsDefault);

    internal async Task<object> Profile(int userId, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.UserId == userId)
            .Select(u => new { u.UserId, u.Email, u.FullName, u.PhoneNumber, u.DateOfBirth, u.Gender, role = u.Role.RoleName })
            .SingleOrDefaultAsync(ct) ?? throw AuthFailure.InvalidSession();

    internal async Task<object> UpdateProfile(int userId, ProfileInput input, CancellationToken ct)
    {
        var fullName = input.FullName.Trim();
        var phone = ValidatePhone(input.PhoneNumber);
        var gender = input.Gender?.Trim();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        if (fullName.Length is < 2 or > 100) throw Invalid("fullName", "Họ tên phải từ 2 đến 100 ký tự.");
        if (input.DateOfBirth is DateOnly birth && (birth > today ||
            today.Year - birth.Year - (today < birth.AddYears(today.Year - birth.Year) ? 1 : 0) > 120))
            throw Invalid("dateOfBirth", "Ngày sinh không hợp lệ.");
        if (gender is not (null or "Male" or "Female" or "Other"))
            throw Invalid("gender", "Giới tính không hợp lệ.");

        var user = await db.Users.Include(u => u.Role).SingleOrDefaultAsync(u => u.UserId == userId, ct)
            ?? throw AuthFailure.InvalidSession();
        user.FullName = fullName;
        user.PhoneNumber = phone;
        user.DateOfBirth = input.DateOfBirth;
        user.Gender = gender;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new { user.UserId, user.Email, user.FullName, user.PhoneNumber, user.DateOfBirth, user.Gender, role = user.Role.RoleName };
    }

    internal async Task<object> Addresses(int userId, AddressQuery filter, CancellationToken ct)
    {
        var rows = Owned(userId).AsNoTracking();
        Expression<Func<UserAddress, object>> key = filter.Sort.TrimStart('-') switch
        {
            "createdAt" => a => a.CreatedAt,
            "isDefault" => a => a.IsDefault,
            _ => a => a.AddressId
        };
        var ordered = filter.Sort.StartsWith('-') ? rows.OrderByDescending(key) : rows.OrderBy(key);
        var totalItems = await rows.CountAsync(ct);
        var offset = (long)(filter.Page - 1) * filter.PageSize;
        var items = offset >= totalItems ? [] : await ordered.ThenBy(a => a.AddressId).Skip((int)offset)
            .Take(filter.PageSize).Select(a => new AddressView(a.AddressId, a.RecipientName, a.PhoneNumber,
                a.Province, a.District, a.Ward, a.AddressLine, a.IsDefault)).ToListAsync(ct);
        return new { items, pagination = new { filter.Page, filter.PageSize, totalItems,
            totalPages = (int)Math.Ceiling((double)totalItems / filter.PageSize) } };
    }

    internal Task<object?> SaveAddress(int userId, int? addressId, AddressInput input, CancellationToken ct) =>
        WriteAddresses(userId, async () =>
        {
            var rows = Owned(userId);
            var now = DateTime.UtcNow;
            var address = addressId is int id ? await rows.AsNoTracking().SingleOrDefaultAsync(a => a.AddressId == id, ct) ?? throw Missing()
                : new UserAddress { UserId = userId, CreatedAt = now };
            var isDefault = addressId is null
                ? input.IsDefault == true || !await rows.AnyAsync(ct)
                : input.IsDefault ?? address.IsDefault;

            address.RecipientName = input.RecipientName.Trim();
            address.PhoneNumber = ValidatePhone(input.PhoneNumber)!;
            address.Province = input.Province.Trim();
            address.District = input.District.Trim();
            address.Ward = input.Ward.Trim();
            address.AddressLine = input.AddressLine.Trim();
            address.UpdatedAt = now;
            address.IsDefault = isDefault;

            if (isDefault)
            {
                // Clear the existing default before SQL Server checks the filtered unique index.
                await rows.Where(a => a.IsDefault && a.AddressId != address.AddressId)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDefault, false).SetProperty(a => a.UpdatedAt, now), ct);
            }
            if (addressId is null)
            {
                db.UserAddresses.Add(address);
                await db.SaveChangesAsync(ct);
            }
            else if (await rows.Where(a => a.AddressId == address.AddressId).ExecuteUpdateAsync(s => s
                .SetProperty(a => a.RecipientName, address.RecipientName).SetProperty(a => a.PhoneNumber, address.PhoneNumber)
                .SetProperty(a => a.Province, address.Province).SetProperty(a => a.District, address.District)
                .SetProperty(a => a.Ward, address.Ward).SetProperty(a => a.AddressLine, address.AddressLine)
                .SetProperty(a => a.IsDefault, address.IsDefault).SetProperty(a => a.UpdatedAt, now), ct) != 1)
                throw Missing();
            return new AddressView(address.AddressId, address.RecipientName, address.PhoneNumber,
                address.Province, address.District, address.Ward, address.AddressLine, address.IsDefault);
        }, ct);

    internal Task<object?> DeleteAddress(int userId, int addressId, CancellationToken ct) =>
        WriteAddresses(userId, async () =>
        {
            if (await Owned(userId).Where(a => a.AddressId == addressId).ExecuteDeleteAsync(ct) != 1) throw Missing();
            return null;
        }, ct);

    private async Task<object?> WriteAddresses(int userId, Func<Task<object?>> change, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                // All address writers for this user take the same lock, even when the book is empty.
                var owner = await db.Database.SqlQuery<int>(
                    $"SELECT UserId AS [Value] FROM dbo.Users WITH (UPDLOCK, HOLDLOCK) WHERE UserId = {userId}")
                    .SingleOrDefaultAsync(ct);
                if (owner == 0) throw AuthFailure.InvalidSession();
                var result = await change();
                await tx.CommitAsync(ct);
                return result;
            }
            catch (Exception ex) when (ex.GetBaseException() is SqlException { Number: 1205 })
            {
                db.ChangeTracker.Clear();
                if (attempt >= 3) throw new ApiFailure(409, "CONCURRENCY_CONFLICT", "Dữ liệu vừa thay đổi, vui lòng thử lại.");
            }
        }
    }

    private IQueryable<UserAddress> Owned(int userId) => db.UserAddresses.Where(a => a.UserId == userId);

    private static string? ValidatePhone(string? phone)
    {
        if (phone is null) return null;
        var normalized = phone.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\A(?:0|\+84)[0-9]{9}\z"))
            throw Invalid("phoneNumber", "Số điện thoại không hợp lệ.");
        return normalized;
    }

    private static ApiFailure Invalid(string field, string message) => new(400, "VALIDATION_ERROR", message, field);
    private static ApiFailure Missing() => new(404, "NOT_FOUND", "Không tìm thấy địa chỉ.");
}
