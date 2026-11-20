using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data.Entities;

namespace SportShop.Api.Data.Seed;

public static class ModelBuilderExtensions
{
    public static void SeedData(this ModelBuilder modelBuilder)
    {
        // 1. Roles (Admin, Customer)
        modelBuilder.Entity<Role>().HasData(
            new Role { RoleId = 1, RoleName = "Admin" },
            new Role { RoleId = 2, RoleName = "Customer" }
        );

        // 2. OrderStatuses (PENDING, CONFIRMED, SHIPPING, DELIVERED, CANCELLED)
        modelBuilder.Entity<OrderStatus>().HasData(
            new OrderStatus { StatusId = 1, StatusCode = "PENDING", StatusName = "Chờ xác nhận", SortOrder = 1, IsFinal = false },
            new OrderStatus { StatusId = 2, StatusCode = "CONFIRMED", StatusName = "Đã xác nhận", SortOrder = 2, IsFinal = false },
            new OrderStatus { StatusId = 3, StatusCode = "SHIPPING", StatusName = "Đang giao hàng", SortOrder = 3, IsFinal = false },
            new OrderStatus { StatusId = 4, StatusCode = "DELIVERED", StatusName = "Giao thành công", SortOrder = 4, IsFinal = true },
            new OrderStatus { StatusId = 5, StatusCode = "CANCELLED", StatusName = "Đã hủy", SortOrder = 5, IsFinal = true }
        );

        // 3. PaymentMethods (COD, BANK_TRANSFER, VNPAY, MOMO)
        var seedDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        modelBuilder.Entity<PaymentMethod>().HasData(
            new PaymentMethod
            {
                PaymentMethodId = 1,
                MethodCode = "COD",
                MethodName = "Thanh toán khi nhận hàng",
                Description = "Thanh toán bằng tiền mặt khi nhận hàng",
                IsActive = true,
                CreatedAt = seedDate,
                UpdatedAt = seedDate
            },
            new PaymentMethod
            {
                PaymentMethodId = 2,
                MethodCode = "BANK_TRANSFER",
                MethodName = "Chuyển khoản ngân hàng",
                Description = "Chuyển khoản qua tài khoản ngân hàng",
                IsActive = true,
                CreatedAt = seedDate,
                UpdatedAt = seedDate
            },
            new PaymentMethod
            {
                PaymentMethodId = 3,
                MethodCode = "VNPAY",
                MethodName = "Ví/Cổng VNPay",
                Description = "Thanh toán qua ví/cổng VNPAY (giai đoạn sau)",
                IsActive = false,
                CreatedAt = seedDate,
                UpdatedAt = seedDate
            },
            new PaymentMethod
            {
                PaymentMethodId = 4,
                MethodCode = "MOMO",
                MethodName = "Ví MoMo",
                Description = "Thanh toán qua ví MoMo (giai đoạn sau)",
                IsActive = false,
                CreatedAt = seedDate,
                UpdatedAt = seedDate
            }
        );
    }
}
