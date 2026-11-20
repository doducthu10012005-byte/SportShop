using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data.Entities;
using SportShop.Api.Data.Seed;

namespace SportShop.Api.Data;

public class SportShopDbContext : DbContext
{
    public SportShopDbContext(DbContextOptions<SportShopDbContext> options) : base(options)
    {
    }

    // 1. Nhóm Tài khoản
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<UserAddress> UserAddresses => Set<UserAddress>();

    // 2. Nhóm Danh mục & Sản phẩm
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Size> Sizes => Set<Size>();
    public DbSet<Color> Colors => Set<Color>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();

    // 3. Nhóm Tồn kho
    public DbSet<Inventory> Inventories => Set<Inventory>();
    public DbSet<InventoryTransaction> InventoryTransactions => Set<InventoryTransaction>();

    // 4. Nhóm Giỏ hàng
    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<CartItem> CartItems => Set<CartItem>();

    // 5. Nhóm Đơn hàng
    public DbSet<OrderStatus> OrderStatuses => Set<OrderStatus>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderStatusHistory> OrderStatusHistories => Set<OrderStatusHistory>();

    // 6. Nhóm Thanh toán
    public DbSet<PaymentMethod> PaymentMethods => Set<PaymentMethod>();
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Toàn bộ DateTime lưu dạng datetime2 (UTC)
        configurationBuilder.Properties<DateTime>().HaveColumnType("datetime2");

        // Toàn bộ tiền tệ dùng precision decimal(18,2)
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {

        base.OnModelCreating(modelBuilder);

        // Cấu hình Database Collation (Mục 1.2 database-design.md: không phân biệt hoa/thường, có phân biệt dấu)
        modelBuilder.UseCollation("Vietnamese_100_CI_AS");

        // ==========================================
        // 1. NHÓM TÀI KHOẢN
        // ==========================================

        // Roles
        modelBuilder.Entity<Role>(entity =>
        {
            entity.ToTable("Roles");
            entity.HasKey(r => r.RoleId);
            entity.Property(r => r.RoleName).HasMaxLength(30).IsRequired();
            entity.HasIndex(r => r.RoleName).IsUnique().HasDatabaseName("UQ_Roles_RoleName");
        });

        // Users
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users", t =>
            {
                t.HasCheckConstraint("CK_Users_Gender", "[Gender] IS NULL OR [Gender] IN ('Male', 'Female', 'Other')");
                t.HasCheckConstraint("CK_Users_TokenVersion", "[TokenVersion] >= 0");
                t.HasCheckConstraint("CK_Users_UpdatedAt", "[UpdatedAt] >= [CreatedAt]");
            });

            entity.HasKey(u => u.UserId);
            entity.Property(u => u.Email).HasMaxLength(256).IsRequired();
            entity.Property(u => u.PasswordHash).HasMaxLength(500).IsRequired();
            entity.Property(u => u.FullName).HasMaxLength(100).IsRequired();
            entity.Property(u => u.PhoneNumber).HasMaxLength(20);
            entity.Property(u => u.Gender).HasMaxLength(10);
            entity.Property(u => u.IsActive).HasDefaultValue(true);
            entity.Property(u => u.MustChangePassword).HasDefaultValue(false);
            entity.Property(u => u.TokenVersion).HasDefaultValue(0);

            entity.HasIndex(u => u.Email).IsUnique().HasDatabaseName("UQ_Users_Email");

            entity.HasOne(u => u.Role)
                  .WithMany(r => r.Users)
                  .HasForeignKey(u => u.RoleId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // RefreshTokens
        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("RefreshTokens", t =>
            {
                t.HasCheckConstraint("CK_RefreshTokens_ExpiresAt", "[ExpiresAt] > [CreatedAt]");
                t.HasCheckConstraint("CK_RefreshTokens_RevokedAt", "[RevokedAt] IS NULL OR [RevokedAt] >= [CreatedAt]");
            });

            entity.HasKey(r => r.RefreshTokenId);
            entity.Property(r => r.TokenHash).HasMaxLength(128).IsRequired();

            entity.HasIndex(r => r.TokenHash).IsUnique().HasDatabaseName("UQ_RefreshTokens_TokenHash");

            entity.HasOne(r => r.User)
                  .WithMany(u => u.RefreshTokens)
                  .HasForeignKey(r => r.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // UserAddresses
        modelBuilder.Entity<UserAddress>(entity =>
        {
            entity.ToTable("UserAddresses", t =>
            {
                t.HasCheckConstraint("CK_UserAddresses_UpdatedAt", "[UpdatedAt] >= [CreatedAt]");
            });

            entity.HasKey(a => a.AddressId);
            entity.Property(a => a.RecipientName).HasMaxLength(100).IsRequired();
            entity.Property(a => a.PhoneNumber).HasMaxLength(20).IsRequired();
            entity.Property(a => a.Province).HasMaxLength(100).IsRequired();
            entity.Property(a => a.District).HasMaxLength(100).IsRequired();
            entity.Property(a => a.Ward).HasMaxLength(100).IsRequired();
            entity.Property(a => a.AddressLine).HasMaxLength(255).IsRequired();
            entity.Property(a => a.IsDefault).HasDefaultValue(false);

            // Tối đa 1 địa chỉ mặc định cho mỗi User (Filtered index)
            entity.HasIndex(a => a.UserId)
                  .IsUnique()
                  .HasFilter("[IsDefault] = 1")
                  .HasDatabaseName("UX_UserAddresses_User_Default");

            entity.HasOne(a => a.User)
                  .WithMany(u => u.Addresses)
                  .HasForeignKey(a => a.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ==========================================
        // 2. NHÓM DANH MỤC & SẢN PHẨM
        // ==========================================

        // Categories
        modelBuilder.Entity<Category>(entity =>
        {
            entity.ToTable("Categories", t =>
            {
                t.HasCheckConstraint("CK_Categories_UpdatedAt", "[UpdatedAt] >= [CreatedAt]");
            });

            entity.HasKey(c => c.CategoryId);
            entity.Property(c => c.CategoryName).HasMaxLength(100).IsRequired();
            entity.Property(c => c.Slug).HasMaxLength(150).IsRequired();
            entity.Property(c => c.Description).HasMaxLength(500);
            entity.Property(c => c.IsActive).HasDefaultValue(true);

            entity.HasIndex(c => c.Slug).IsUnique().HasDatabaseName("UQ_Categories_Slug");

            entity.HasOne(c => c.ParentCategory)
                  .WithMany(c => c.SubCategories)
                  .HasForeignKey(c => c.ParentCategoryId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // Brands
        modelBuilder.Entity<Brand>(entity =>
        {
            entity.ToTable("Brands", t =>
            {
                t.HasCheckConstraint("CK_Brands_UpdatedAt", "[UpdatedAt] >= [CreatedAt]");
            });

            entity.HasKey(b => b.BrandId);
            entity.Property(b => b.BrandName).HasMaxLength(100).IsRequired();
            entity.Property(b => b.Slug).HasMaxLength(150).IsRequired();
            entity.Property(b => b.LogoUrl).HasMaxLength(500);
            entity.Property(b => b.Description).HasMaxLength(500);
            entity.Property(b => b.IsActive).HasDefaultValue(true);

            entity.HasIndex(b => b.BrandName).IsUnique().HasDatabaseName("UQ_Brands_BrandName");
            entity.HasIndex(b => b.Slug).IsUnique().HasDatabaseName("UQ_Brands_Slug");
        });

        // Products
        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("Products", t =>
            {
                t.HasCheckConstraint("CK_Products_UpdatedAt", "[UpdatedAt] >= [CreatedAt]");
            });

            entity.HasKey(p => p.ProductId);
            entity.Property(p => p.ProductName).HasMaxLength(200).IsRequired();
            entity.Property(p => p.Slug).HasMaxLength(250).IsRequired();
            entity.Property(p => p.Description).HasColumnType("nvarchar(max)");
            entity.Property(p => p.IsActive).HasDefaultValue(true);

            entity.HasIndex(p => p.Slug).IsUnique().HasDatabaseName("UQ_Products_Slug");
            entity.HasIndex(p => new { p.CategoryId, p.IsActive }).HasDatabaseName("IX_Products_Category_IsActive");
            entity.HasIndex(p => new { p.BrandId, p.IsActive }).HasDatabaseName("IX_Products_Brand_IsActive");
            entity.HasIndex(p => p.ProductName).HasDatabaseName("IX_Products_ProductName");

            entity.HasOne(p => p.Category)
                  .WithMany(c => c.Products)
                  .HasForeignKey(p => p.CategoryId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(p => p.Brand)
                  .WithMany(b => b.Products)
                  .HasForeignKey(p => p.BrandId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // Sizes
        modelBuilder.Entity<Size>(entity =>
        {
            entity.ToTable("Sizes", t =>
            {
                t.HasCheckConstraint("CK_Sizes_SortOrder", "[SortOrder] >= 0");
                t.HasCheckConstraint("CK_Sizes_UpdatedAt", "[UpdatedAt] >= [CreatedAt]");
            });

            entity.HasKey(s => s.SizeId);
            entity.Property(s => s.SizeName).HasMaxLength(20).IsRequired();
            entity.Property(s => s.SortOrder).HasDefaultValue(0);
            entity.Property(s => s.IsActive).HasDefaultValue(true);

            entity.HasIndex(s => s.SizeName).IsUnique().HasDatabaseName("UQ_Sizes_SizeName");
        });

        // Colors
        modelBuilder.Entity<Color>(entity =>
        {
            entity.ToTable("Colors", t =>
            {
                t.HasCheckConstraint("CK_Colors_HexCode", "[HexCode] IS NULL OR [HexCode] LIKE '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'");
                t.HasCheckConstraint("CK_Colors_UpdatedAt", "[UpdatedAt] >= [CreatedAt]");
            });

            entity.HasKey(c => c.ColorId);
            entity.Property(c => c.ColorName).HasMaxLength(50).IsRequired();
            entity.Property(c => c.HexCode).HasMaxLength(7).IsFixedLength();
            entity.Property(c => c.IsActive).HasDefaultValue(true);

            entity.HasIndex(c => c.ColorName).IsUnique().HasDatabaseName("UQ_Colors_ColorName");
        });

        // ProductVariants
        modelBuilder.Entity<ProductVariant>(entity =>
        {
            entity.ToTable("ProductVariants", t =>
            {
                t.HasCheckConstraint("CK_ProductVariants_Price", "[Price] >= 0");
                t.HasCheckConstraint("CK_ProductVariants_UpdatedAt", "[UpdatedAt] >= [CreatedAt]");
            });

            entity.HasKey(pv => pv.VariantId);
            entity.Property(pv => pv.Sku).HasMaxLength(50).IsRequired();
            entity.Property(pv => pv.IsActive).HasDefaultValue(true);

            entity.HasIndex(pv => pv.Sku).IsUnique().HasDatabaseName("UQ_ProductVariants_Sku");
            entity.HasIndex(pv => new { pv.ProductId, pv.SizeId, pv.ColorId }).IsUnique().HasDatabaseName("UQ_ProductVariants_Product_Size_Color");

            entity.HasIndex(pv => new { pv.ProductId, pv.IsActive })
                  .IncludeProperties(pv => pv.Price)
                  .HasDatabaseName("IX_ProductVariants_Product_IsActive");

            entity.HasIndex(pv => pv.SizeId).HasDatabaseName("IX_ProductVariants_SizeId");
            entity.HasIndex(pv => pv.ColorId).HasDatabaseName("IX_ProductVariants_ColorId");

            entity.HasOne(pv => pv.Product)
                  .WithMany(p => p.Variants)
                  .HasForeignKey(pv => pv.ProductId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(pv => pv.Size)
                  .WithMany(s => s.Variants)
                  .HasForeignKey(pv => pv.SizeId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(pv => pv.Color)
                  .WithMany(c => c.Variants)
                  .HasForeignKey(pv => pv.ColorId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // ProductImages
        modelBuilder.Entity<ProductImage>(entity =>
        {
            entity.ToTable("ProductImages", t =>
            {
                t.HasCheckConstraint("CK_ProductImages_SortOrder", "[SortOrder] >= 0");
                t.HasCheckConstraint("CK_ProductImages_UpdatedAt", "[UpdatedAt] >= [CreatedAt]");
            });

            entity.HasKey(pi => pi.ImageId);
            entity.Property(pi => pi.ImageUrl).HasMaxLength(500).IsRequired();
            entity.Property(pi => pi.IsPrimary).HasDefaultValue(false);
            entity.Property(pi => pi.SortOrder).HasDefaultValue(0);

            entity.HasIndex(pi => pi.ImageUrl).IsUnique().HasDatabaseName("UQ_ProductImages_ImageUrl");

            // Tối đa 1 ảnh đại diện cho mỗi sản phẩm (Filtered index)
            entity.HasIndex(pi => pi.ProductId)
                  .IsUnique()
                  .HasFilter("[IsPrimary] = 1")
                  .HasDatabaseName("UX_ProductImages_Primary");

            entity.HasIndex(pi => new { pi.ProductId, pi.ColorId, pi.SortOrder })
                  .IsUnique()
                  .HasDatabaseName("UQ_ProductImages_Product_Color_SortOrder");

            entity.HasOne(pi => pi.Product)
                  .WithMany(p => p.Images)
                  .HasForeignKey(pi => pi.ProductId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(pi => pi.Color)
                  .WithMany(c => c.Images)
                  .HasForeignKey(pi => pi.ColorId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // ==========================================
        // 3. NHÓM TỒN KHO
        // ==========================================

        // Inventories
        modelBuilder.Entity<Inventory>(entity =>
        {
            entity.ToTable("Inventories", t =>
            {
                t.HasCheckConstraint("CK_Inventories_QuantityOnHand", "[QuantityOnHand] >= 0");
                t.HasCheckConstraint("CK_Inventories_LowStockThreshold", "[LowStockThreshold] >= 0");
                t.HasCheckConstraint("CK_Inventories_UpdatedAt", "[UpdatedAt] >= [CreatedAt]");
            });

            entity.HasKey(i => i.InventoryId);
            entity.Property(i => i.LowStockThreshold).HasDefaultValue(5);
            entity.Property(i => i.RowVersion).IsRowVersion();

            entity.HasIndex(i => i.VariantId).IsUnique().HasDatabaseName("UQ_Inventories_VariantId");

            entity.HasOne(i => i.Variant)
                  .WithOne(pv => pv.Inventory)
                  .HasForeignKey<Inventory>(i => i.VariantId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // InventoryTransactions
        modelBuilder.Entity<InventoryTransaction>(entity =>
        {
            entity.ToTable("InventoryTransactions", t =>
            {
                t.HasCheckConstraint("CK_InventoryTransactions_QuantityChange", "[QuantityChange] <> 0");
                t.HasCheckConstraint("CK_InventoryTransactions_QuantityAfter", "[QuantityAfter] >= 0");
                t.HasCheckConstraint("CK_InventoryTransactions_Type", "[TransactionType] IN ('Import', 'Sale', 'OrderRestock', 'Adjustment')");
                t.HasCheckConstraint("CK_InventoryTransactions_TypeRules",
                    "([TransactionType] = 'Sale' AND [QuantityChange] < 0 AND [OrderItemId] IS NOT NULL) OR " +
                    "([TransactionType] = 'OrderRestock' AND [QuantityChange] > 0 AND [OrderItemId] IS NOT NULL) OR " +
                    "([TransactionType] = 'Import' AND [QuantityChange] > 0 AND [OrderItemId] IS NULL) OR " +
                    "([TransactionType] = 'Adjustment' AND [OrderItemId] IS NULL)");
            });

            entity.HasKey(t => t.TransactionId);
            entity.Property(t => t.TransactionType).HasMaxLength(20).IsRequired();
            entity.Property(t => t.Note).HasMaxLength(255);

            // Chống hoàn kho hoặc bán trùng cho 1 dòng đơn hàng (Filtered index)
            entity.HasIndex(t => new { t.OrderItemId, t.TransactionType })
                  .IsUnique()
                  .HasFilter("[OrderItemId] IS NOT NULL")
                  .HasDatabaseName("UX_InventoryTransactions_OrderItem_Type");

            entity.HasIndex(t => new { t.VariantId, t.CreatedAt }).HasDatabaseName("IX_InventoryTransactions_Variant_CreatedAt");

            entity.HasOne(t => t.Variant)
                  .WithMany(pv => pv.InventoryTransactions)
                  .HasForeignKey(t => t.VariantId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.OrderItem)
                  .WithMany(oi => oi.InventoryTransactions)
                  .HasForeignKey(t => t.OrderItemId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.User)
                  .WithMany(u => u.InventoryTransactions)
                  .HasForeignKey(t => t.CreatedBy)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // ==========================================
        // 4. NHÓM GIỎ HÀNG
        // ==========================================

        // Carts
        modelBuilder.Entity<Cart>(entity =>
        {
            entity.ToTable("Carts", t =>
            {
                t.HasCheckConstraint("CK_Carts_UpdatedAt", "[UpdatedAt] >= [CreatedAt]");
            });

            entity.HasKey(c => c.CartId);
            entity.HasIndex(c => c.UserId).IsUnique().HasDatabaseName("UQ_Carts_UserId");

            entity.HasOne(c => c.User)
                  .WithOne(u => u.Cart)
                  .HasForeignKey<Cart>(c => c.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // CartItems
        modelBuilder.Entity<CartItem>(entity =>
        {
            entity.ToTable("CartItems", t =>
            {
                t.HasCheckConstraint("CK_CartItems_Quantity", "[Quantity] >= 1 AND [Quantity] <= 99");
                t.HasCheckConstraint("CK_CartItems_UpdatedAt", "[UpdatedAt] >= [CreatedAt]");
            });

            entity.HasKey(ci => ci.CartItemId);

            entity.HasIndex(ci => new { ci.CartId, ci.VariantId }).IsUnique().HasDatabaseName("UQ_CartItems_Cart_Variant");
            entity.HasIndex(ci => ci.VariantId).HasDatabaseName("IX_CartItems_VariantId");

            entity.HasOne(ci => ci.Cart)
                  .WithMany(c => c.Items)
                  .HasForeignKey(ci => ci.CartId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(ci => ci.Variant)
                  .WithMany(pv => pv.CartItems)
                  .HasForeignKey(ci => ci.VariantId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // ==========================================
        // 5. NHÓM ĐƠN HÀNG
        // ==========================================

        // OrderStatuses
        modelBuilder.Entity<OrderStatus>(entity =>
        {
            entity.ToTable("OrderStatuses");
            entity.HasKey(os => os.StatusId);
            entity.Property(os => os.StatusId).ValueGeneratedNever();
            entity.Property(os => os.StatusCode).HasMaxLength(30).IsRequired();
            entity.Property(os => os.StatusName).HasMaxLength(50).IsRequired();

            entity.HasIndex(os => os.StatusCode).IsUnique().HasDatabaseName("UQ_OrderStatuses_StatusCode");
        });

        // Orders
        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders", t =>
            {
                t.HasCheckConstraint("CK_Orders_Subtotal", "[Subtotal] >= 0");
                t.HasCheckConstraint("CK_Orders_ShippingFee", "[ShippingFee] >= 0");
                t.HasCheckConstraint("CK_Orders_TotalAmount", "[TotalAmount] = [Subtotal] + [ShippingFee]");
                t.HasCheckConstraint("CK_Orders_DeliveredAt", "([StatusId] = 4 AND [DeliveredAt] IS NOT NULL) OR ([StatusId] <> 4)");
                t.HasCheckConstraint("CK_Orders_CancelReason", "([StatusId] = 5 AND [CancelReason] IS NOT NULL) OR ([StatusId] <> 5)");
                t.HasCheckConstraint("CK_Orders_UpdatedAt", "[UpdatedAt] >= [CreatedAt]");
            });

            entity.HasKey(o => o.OrderId);
            entity.Property(o => o.OrderCode).HasMaxLength(30).IsRequired();
            entity.Property(o => o.RecipientName).HasMaxLength(100).IsRequired();
            entity.Property(o => o.RecipientPhone).HasMaxLength(20).IsRequired();
            entity.Property(o => o.ShippingAddress).HasMaxLength(500).IsRequired();
            entity.Property(o => o.Note).HasMaxLength(500);
            entity.Property(o => o.ShippingFee).HasDefaultValue(0m);
            entity.Property(o => o.CancelReason).HasMaxLength(255);
            entity.Property(o => o.RowVersion).IsRowVersion();

            entity.HasIndex(o => o.OrderCode).IsUnique().HasDatabaseName("UQ_Orders_OrderCode");
            entity.HasIndex(o => new { o.UserId, o.CreatedAt }).HasDatabaseName("IX_Orders_UserId_CreatedAt");
            entity.HasIndex(o => new { o.StatusId, o.CreatedAt }).HasDatabaseName("IX_Orders_StatusId_CreatedAt");

            // Index phủ cho báo cáo doanh thu theo DeliveredAt
            entity.HasIndex(o => o.DeliveredAt)
                  .HasFilter("[DeliveredAt] IS NOT NULL")
                  .IncludeProperties(o => new { o.Subtotal, o.ShippingFee, o.TotalAmount })
                  .HasDatabaseName("IX_Orders_DeliveredAt_Revenue");

            entity.HasOne(o => o.User)
                  .WithMany(u => u.Orders)
                  .HasForeignKey(o => o.UserId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(o => o.Status)
                  .WithMany(os => os.Orders)
                  .HasForeignKey(o => o.StatusId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // OrderItems
        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.ToTable("OrderItems", t =>
            {
                t.HasCheckConstraint("CK_OrderItems_UnitPrice", "[UnitPrice] >= 0");
                t.HasCheckConstraint("CK_OrderItems_Quantity", "[Quantity] > 0");
            });

            entity.HasKey(oi => oi.OrderItemId);
            entity.Property(oi => oi.ProductName).HasMaxLength(200).IsRequired();
            entity.Property(oi => oi.Sku).HasMaxLength(50).IsRequired();
            entity.Property(oi => oi.SizeName).HasMaxLength(20).IsRequired();
            entity.Property(oi => oi.ColorName).HasMaxLength(50).IsRequired();
            entity.Property(oi => oi.ImageUrl).HasMaxLength(500);

            // Cột tính toán lưu trữ (Computed Column Persisted)
            entity.Property(oi => oi.LineTotal)
                  .HasComputedColumnSql("[UnitPrice] * [Quantity]", stored: true);

            entity.HasIndex(oi => oi.OrderId).HasDatabaseName("IX_OrderItems_OrderId");
            entity.HasIndex(oi => oi.VariantId).HasDatabaseName("IX_OrderItems_VariantId");

            // Filtered index kiểm tra nhanh ảnh tham chiếu trước khi dọn file
            entity.HasIndex(oi => oi.ImageUrl)
                  .HasFilter("[ImageUrl] IS NOT NULL")
                  .HasDatabaseName("IX_OrderItems_ImageUrl");

            entity.HasOne(oi => oi.Order)
                  .WithMany(o => o.Items)
                  .HasForeignKey(oi => oi.OrderId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(oi => oi.Variant)
                  .WithMany(pv => pv.OrderItems)
                  .HasForeignKey(oi => oi.VariantId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // OrderStatusHistories
        modelBuilder.Entity<OrderStatusHistory>(entity =>
        {
            entity.ToTable("OrderStatusHistories");
            entity.HasKey(h => h.HistoryId);
            entity.Property(h => h.Note).HasMaxLength(255);

            entity.HasIndex(h => h.OrderId).HasDatabaseName("IX_OrderStatusHistories_OrderId");

            entity.HasOne(h => h.Order)
                  .WithMany(o => o.StatusHistories)
                  .HasForeignKey(h => h.OrderId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(h => h.FromStatus)
                  .WithMany(os => os.FromStatusHistories)
                  .HasForeignKey(h => h.FromStatusId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(h => h.ToStatus)
                  .WithMany(os => os.ToStatusHistories)
                  .HasForeignKey(h => h.ToStatusId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(h => h.ChangedByUser)
                  .WithMany()
                  .HasForeignKey(h => h.ChangedBy)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // ==========================================
        // 6. NHÓM THANH TOÁN
        // ==========================================

        // PaymentMethods
        modelBuilder.Entity<PaymentMethod>(entity =>
        {
            entity.ToTable("PaymentMethods", t =>
            {
                t.HasCheckConstraint("CK_PaymentMethods_UpdatedAt", "[UpdatedAt] >= [CreatedAt]");
            });

            entity.HasKey(pm => pm.PaymentMethodId);
            entity.Property(pm => pm.MethodCode).HasMaxLength(30).IsRequired();
            entity.Property(pm => pm.MethodName).HasMaxLength(100).IsRequired();
            entity.Property(pm => pm.Description).HasMaxLength(255);

            entity.HasIndex(pm => pm.MethodCode).IsUnique().HasDatabaseName("UQ_PaymentMethods_MethodCode");
        });

        // Payments
        modelBuilder.Entity<Payment>(entity =>
        {
            entity.ToTable("Payments", t =>
            {
                t.HasCheckConstraint("CK_Payments_Amount", "[Amount] >= 0");
                t.HasCheckConstraint("CK_Payments_ReceivedAmount", "[ReceivedAmount] IS NULL OR [ReceivedAmount] >= 0");
                t.HasCheckConstraint("CK_Payments_Status", "[PaymentStatus] IN ('Pending', 'Paid', 'Failed', 'Expired', 'RefundPending', 'Refunded')");
                t.HasCheckConstraint("CK_Payments_Status_ReceivedAmount_PaidAt",
                    "([PaymentStatus] IN ('Paid', 'RefundPending', 'Refunded') AND [ReceivedAmount] = [Amount] AND [PaidAt] IS NOT NULL) OR " +
                    "([PaymentStatus] IN ('Pending', 'Failed', 'Expired') AND [ReceivedAmount] IS NULL AND [PaidAt] IS NULL)");
                t.HasCheckConstraint("CK_Payments_StrictExpiration",
                    "([PaymentStatus] IN ('Paid', 'RefundPending', 'Refunded') AND [ExpiresAt] IS NOT NULL AND [PaidAt] < [ExpiresAt]) OR " +
                    "([PaymentStatus] NOT IN ('Paid', 'RefundPending', 'Refunded') OR [ExpiresAt] IS NULL)");
                t.HasCheckConstraint("CK_Payments_Expired_ExpiresAt",
                    "([PaymentStatus] = 'Expired' AND [ExpiresAt] IS NOT NULL) OR ([PaymentStatus] <> 'Expired')");
                t.HasCheckConstraint("CK_Payments_ExpiresAt_CreatedAt",
                    "[ExpiresAt] IS NULL OR [ExpiresAt] > [CreatedAt]");
                t.HasCheckConstraint("CK_Payments_Refunded",
                    "([PaymentStatus] = 'Refunded' AND [RefundedAt] IS NOT NULL) OR ([PaymentStatus] <> 'Refunded' AND [RefundedAt] IS NULL)");
                t.HasCheckConstraint("CK_Payments_RefundedAt_PaidAt",
                    "[RefundedAt] IS NULL OR [PaidAt] IS NULL OR [RefundedAt] >= [PaidAt]");
                t.HasCheckConstraint("CK_Payments_UpdatedAt", "[UpdatedAt] >= [CreatedAt]");
            });

            entity.HasKey(p => p.PaymentId);
            entity.Property(p => p.PaymentStatus).HasMaxLength(20).IsRequired();
            entity.Property(p => p.TransactionCode).HasMaxLength(100);

            // Mỗi đơn tối đa 1 khoản thanh toán 'sống' trong toàn bộ lịch sử (Filtered index)
            // SQL Server không hỗ trợ IN/OR trong filtered index -> dùng <> nối AND đúng theo tài liệu
            entity.HasIndex(p => p.OrderId)
                  .IsUnique()
                  .HasFilter("[PaymentStatus] <> 'Failed' AND [PaymentStatus] <> 'Expired'")
                  .HasDatabaseName("UX_Payments_Order_Live");

            // Một mã giao dịch chỉ dùng cho 1 khoản thanh toán (Filtered index)
            entity.HasIndex(p => new { p.PaymentMethodId, p.TransactionCode })
                  .IsUnique()
                  .HasFilter("[TransactionCode] IS NOT NULL")
                  .HasDatabaseName("UX_Payments_Method_TransactionCode");

            // Filtered index quét nhanh khoản chuyển khoản hết hạn
            entity.HasIndex(p => p.ExpiresAt)
                  .HasFilter("[PaymentStatus] = 'Pending' AND [ExpiresAt] IS NOT NULL")
                  .HasDatabaseName("IX_Payments_Pending_ExpiresAt");

            // Filtered index danh sách khoản cần hoàn tiền
            entity.HasIndex(p => p.UpdatedAt)
                  .HasFilter("[PaymentStatus] = 'RefundPending'")
                  .HasDatabaseName("IX_Payments_RefundPending");

            entity.HasIndex(p => new { p.OrderId, p.CreatedAt }).HasDatabaseName("IX_Payments_OrderId_CreatedAt");

            entity.HasOne(p => p.Order)
                  .WithMany(o => o.Payments)
                  .HasForeignKey(p => p.OrderId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(p => p.PaymentMethod)
                  .WithMany(pm => pm.Payments)
                  .HasForeignKey(p => p.PaymentMethodId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // ==========================================
        // 7. SEED DATA CỐ ĐỊNH (Không chứa Admin)
        // ==========================================
        modelBuilder.SeedData();
    }
}
