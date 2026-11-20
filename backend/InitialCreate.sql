IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
CREATE TABLE [Brands] (
    [BrandId] int NOT NULL IDENTITY,
    [BrandName] nvarchar(100) NOT NULL,
    [Slug] nvarchar(150) NOT NULL,
    [LogoUrl] nvarchar(500) NULL,
    [Description] nvarchar(500) NULL,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_Brands] PRIMARY KEY ([BrandId]),
    CONSTRAINT [CK_Brands_UpdatedAt] CHECK ([UpdatedAt] >= [CreatedAt])
);

CREATE TABLE [Categories] (
    [CategoryId] int NOT NULL IDENTITY,
    [ParentCategoryId] int NULL,
    [CategoryName] nvarchar(100) NOT NULL,
    [Slug] nvarchar(150) NOT NULL,
    [Description] nvarchar(500) NULL,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_Categories] PRIMARY KEY ([CategoryId]),
    CONSTRAINT [CK_Categories_UpdatedAt] CHECK ([UpdatedAt] >= [CreatedAt]),
    CONSTRAINT [FK_Categories_Categories_ParentCategoryId] FOREIGN KEY ([ParentCategoryId]) REFERENCES [Categories] ([CategoryId]) ON DELETE NO ACTION
);

CREATE TABLE [Colors] (
    [ColorId] int NOT NULL IDENTITY,
    [ColorName] nvarchar(50) NOT NULL,
    [HexCode] nchar(7) NULL,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_Colors] PRIMARY KEY ([ColorId]),
    CONSTRAINT [CK_Colors_HexCode] CHECK ([HexCode] IS NULL OR [HexCode] LIKE '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'),
    CONSTRAINT [CK_Colors_UpdatedAt] CHECK ([UpdatedAt] >= [CreatedAt])
);

CREATE TABLE [OrderStatuses] (
    [StatusId] int NOT NULL,
    [StatusCode] nvarchar(30) NOT NULL,
    [StatusName] nvarchar(50) NOT NULL,
    [SortOrder] int NOT NULL,
    [IsFinal] bit NOT NULL,
    CONSTRAINT [PK_OrderStatuses] PRIMARY KEY ([StatusId])
);

CREATE TABLE [PaymentMethods] (
    [PaymentMethodId] int NOT NULL IDENTITY,
    [MethodCode] nvarchar(30) NOT NULL,
    [MethodName] nvarchar(100) NOT NULL,
    [Description] nvarchar(255) NULL,
    [IsActive] bit NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_PaymentMethods] PRIMARY KEY ([PaymentMethodId]),
    CONSTRAINT [CK_PaymentMethods_UpdatedAt] CHECK ([UpdatedAt] >= [CreatedAt])
);

CREATE TABLE [Roles] (
    [RoleId] int NOT NULL IDENTITY,
    [RoleName] nvarchar(30) NOT NULL,
    CONSTRAINT [PK_Roles] PRIMARY KEY ([RoleId])
);

CREATE TABLE [Sizes] (
    [SizeId] int NOT NULL IDENTITY,
    [SizeName] nvarchar(20) NOT NULL,
    [SortOrder] int NOT NULL DEFAULT 0,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_Sizes] PRIMARY KEY ([SizeId]),
    CONSTRAINT [CK_Sizes_SortOrder] CHECK ([SortOrder] >= 0),
    CONSTRAINT [CK_Sizes_UpdatedAt] CHECK ([UpdatedAt] >= [CreatedAt])
);

CREATE TABLE [Products] (
    [ProductId] int NOT NULL IDENTITY,
    [CategoryId] int NOT NULL,
    [BrandId] int NOT NULL,
    [ProductName] nvarchar(200) NOT NULL,
    [Slug] nvarchar(250) NOT NULL,
    [Description] nvarchar(max) NULL,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_Products] PRIMARY KEY ([ProductId]),
    CONSTRAINT [CK_Products_UpdatedAt] CHECK ([UpdatedAt] >= [CreatedAt]),
    CONSTRAINT [FK_Products_Brands_BrandId] FOREIGN KEY ([BrandId]) REFERENCES [Brands] ([BrandId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Products_Categories_CategoryId] FOREIGN KEY ([CategoryId]) REFERENCES [Categories] ([CategoryId]) ON DELETE NO ACTION
);

CREATE TABLE [Users] (
    [UserId] int NOT NULL IDENTITY,
    [RoleId] int NOT NULL,
    [Email] nvarchar(256) NOT NULL,
    [PasswordHash] nvarchar(500) NOT NULL,
    [FullName] nvarchar(100) NOT NULL,
    [PhoneNumber] nvarchar(20) NULL,
    [DateOfBirth] date NULL,
    [Gender] nvarchar(10) NULL,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    [MustChangePassword] bit NOT NULL DEFAULT CAST(0 AS bit),
    [TokenVersion] int NOT NULL DEFAULT 0,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    [LastLoginAt] datetime2 NULL,
    CONSTRAINT [PK_Users] PRIMARY KEY ([UserId]),
    CONSTRAINT [CK_Users_Gender] CHECK ([Gender] IS NULL OR [Gender] IN ('Male', 'Female', 'Other')),
    CONSTRAINT [CK_Users_TokenVersion] CHECK ([TokenVersion] >= 0),
    CONSTRAINT [CK_Users_UpdatedAt] CHECK ([UpdatedAt] >= [CreatedAt]),
    CONSTRAINT [FK_Users_Roles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [Roles] ([RoleId]) ON DELETE NO ACTION
);

CREATE TABLE [ProductImages] (
    [ImageId] int NOT NULL IDENTITY,
    [ProductId] int NOT NULL,
    [ColorId] int NULL,
    [ImageUrl] nvarchar(500) NOT NULL,
    [IsPrimary] bit NOT NULL DEFAULT CAST(0 AS bit),
    [SortOrder] int NOT NULL DEFAULT 0,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_ProductImages] PRIMARY KEY ([ImageId]),
    CONSTRAINT [CK_ProductImages_SortOrder] CHECK ([SortOrder] >= 0),
    CONSTRAINT [CK_ProductImages_UpdatedAt] CHECK ([UpdatedAt] >= [CreatedAt]),
    CONSTRAINT [FK_ProductImages_Colors_ColorId] FOREIGN KEY ([ColorId]) REFERENCES [Colors] ([ColorId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_ProductImages_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products] ([ProductId]) ON DELETE CASCADE
);

CREATE TABLE [ProductVariants] (
    [VariantId] int NOT NULL IDENTITY,
    [ProductId] int NOT NULL,
    [SizeId] int NOT NULL,
    [ColorId] int NOT NULL,
    [Sku] nvarchar(50) NOT NULL,
    [Price] decimal(18,2) NOT NULL,
    [IsActive] bit NOT NULL DEFAULT CAST(1 AS bit),
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_ProductVariants] PRIMARY KEY ([VariantId]),
    CONSTRAINT [CK_ProductVariants_Price] CHECK ([Price] >= 0),
    CONSTRAINT [CK_ProductVariants_UpdatedAt] CHECK ([UpdatedAt] >= [CreatedAt]),
    CONSTRAINT [FK_ProductVariants_Colors_ColorId] FOREIGN KEY ([ColorId]) REFERENCES [Colors] ([ColorId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_ProductVariants_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products] ([ProductId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_ProductVariants_Sizes_SizeId] FOREIGN KEY ([SizeId]) REFERENCES [Sizes] ([SizeId]) ON DELETE NO ACTION
);

CREATE TABLE [Carts] (
    [CartId] int NOT NULL IDENTITY,
    [UserId] int NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_Carts] PRIMARY KEY ([CartId]),
    CONSTRAINT [CK_Carts_UpdatedAt] CHECK ([UpdatedAt] >= [CreatedAt]),
    CONSTRAINT [FK_Carts_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([UserId]) ON DELETE CASCADE
);

CREATE TABLE [Orders] (
    [OrderId] int NOT NULL IDENTITY,
    [OrderCode] nvarchar(30) NOT NULL,
    [UserId] int NOT NULL,
    [StatusId] int NOT NULL,
    [RecipientName] nvarchar(100) NOT NULL,
    [RecipientPhone] nvarchar(20) NOT NULL,
    [ShippingAddress] nvarchar(500) NOT NULL,
    [Note] nvarchar(500) NULL,
    [Subtotal] decimal(18,2) NOT NULL,
    [ShippingFee] decimal(18,2) NOT NULL DEFAULT 0.0,
    [TotalAmount] decimal(18,2) NOT NULL,
    [CancelReason] nvarchar(255) NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    [DeliveredAt] datetime2 NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Orders] PRIMARY KEY ([OrderId]),
    CONSTRAINT [CK_Orders_CancelReason] CHECK (([StatusId] = 5 AND [CancelReason] IS NOT NULL) OR ([StatusId] <> 5)),
    CONSTRAINT [CK_Orders_DeliveredAt] CHECK (([StatusId] = 4 AND [DeliveredAt] IS NOT NULL) OR ([StatusId] <> 4)),
    CONSTRAINT [CK_Orders_ShippingFee] CHECK ([ShippingFee] >= 0),
    CONSTRAINT [CK_Orders_Subtotal] CHECK ([Subtotal] >= 0),
    CONSTRAINT [CK_Orders_TotalAmount] CHECK ([TotalAmount] = [Subtotal] + [ShippingFee]),
    CONSTRAINT [CK_Orders_UpdatedAt] CHECK ([UpdatedAt] >= [CreatedAt]),
    CONSTRAINT [FK_Orders_OrderStatuses_StatusId] FOREIGN KEY ([StatusId]) REFERENCES [OrderStatuses] ([StatusId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Orders_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([UserId]) ON DELETE NO ACTION
);

CREATE TABLE [RefreshTokens] (
    [RefreshTokenId] int NOT NULL IDENTITY,
    [UserId] int NOT NULL,
    [TokenHash] nvarchar(128) NOT NULL,
    [ExpiresAt] datetime2 NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [RevokedAt] datetime2 NULL,
    CONSTRAINT [PK_RefreshTokens] PRIMARY KEY ([RefreshTokenId]),
    CONSTRAINT [CK_RefreshTokens_ExpiresAt] CHECK ([ExpiresAt] > [CreatedAt]),
    CONSTRAINT [CK_RefreshTokens_RevokedAt] CHECK ([RevokedAt] IS NULL OR [RevokedAt] >= [CreatedAt]),
    CONSTRAINT [FK_RefreshTokens_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([UserId]) ON DELETE CASCADE
);

CREATE TABLE [UserAddresses] (
    [AddressId] int NOT NULL IDENTITY,
    [UserId] int NOT NULL,
    [RecipientName] nvarchar(100) NOT NULL,
    [PhoneNumber] nvarchar(20) NOT NULL,
    [Province] nvarchar(100) NOT NULL,
    [District] nvarchar(100) NOT NULL,
    [Ward] nvarchar(100) NOT NULL,
    [AddressLine] nvarchar(255) NOT NULL,
    [IsDefault] bit NOT NULL DEFAULT CAST(0 AS bit),
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_UserAddresses] PRIMARY KEY ([AddressId]),
    CONSTRAINT [CK_UserAddresses_UpdatedAt] CHECK ([UpdatedAt] >= [CreatedAt]),
    CONSTRAINT [FK_UserAddresses_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([UserId]) ON DELETE CASCADE
);

CREATE TABLE [Inventories] (
    [InventoryId] int NOT NULL IDENTITY,
    [VariantId] int NOT NULL,
    [QuantityOnHand] int NOT NULL,
    [LowStockThreshold] int NOT NULL DEFAULT 5,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Inventories] PRIMARY KEY ([InventoryId]),
    CONSTRAINT [CK_Inventories_LowStockThreshold] CHECK ([LowStockThreshold] >= 0),
    CONSTRAINT [CK_Inventories_QuantityOnHand] CHECK ([QuantityOnHand] >= 0),
    CONSTRAINT [CK_Inventories_UpdatedAt] CHECK ([UpdatedAt] >= [CreatedAt]),
    CONSTRAINT [FK_Inventories_ProductVariants_VariantId] FOREIGN KEY ([VariantId]) REFERENCES [ProductVariants] ([VariantId]) ON DELETE NO ACTION
);

CREATE TABLE [CartItems] (
    [CartItemId] int NOT NULL IDENTITY,
    [CartId] int NOT NULL,
    [VariantId] int NOT NULL,
    [Quantity] int NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_CartItems] PRIMARY KEY ([CartItemId]),
    CONSTRAINT [CK_CartItems_Quantity] CHECK ([Quantity] >= 1 AND [Quantity] <= 99),
    CONSTRAINT [CK_CartItems_UpdatedAt] CHECK ([UpdatedAt] >= [CreatedAt]),
    CONSTRAINT [FK_CartItems_Carts_CartId] FOREIGN KEY ([CartId]) REFERENCES [Carts] ([CartId]) ON DELETE CASCADE,
    CONSTRAINT [FK_CartItems_ProductVariants_VariantId] FOREIGN KEY ([VariantId]) REFERENCES [ProductVariants] ([VariantId]) ON DELETE NO ACTION
);

CREATE TABLE [OrderItems] (
    [OrderItemId] int NOT NULL IDENTITY,
    [OrderId] int NOT NULL,
    [VariantId] int NOT NULL,
    [ProductName] nvarchar(200) NOT NULL,
    [Sku] nvarchar(50) NOT NULL,
    [SizeName] nvarchar(20) NOT NULL,
    [ColorName] nvarchar(50) NOT NULL,
    [ImageUrl] nvarchar(500) NULL,
    [UnitPrice] decimal(18,2) NOT NULL,
    [Quantity] int NOT NULL,
    [LineTotal] AS [UnitPrice] * [Quantity] PERSISTED,
    CONSTRAINT [PK_OrderItems] PRIMARY KEY ([OrderItemId]),
    CONSTRAINT [CK_OrderItems_Quantity] CHECK ([Quantity] > 0),
    CONSTRAINT [CK_OrderItems_UnitPrice] CHECK ([UnitPrice] >= 0),
    CONSTRAINT [FK_OrderItems_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([OrderId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_OrderItems_ProductVariants_VariantId] FOREIGN KEY ([VariantId]) REFERENCES [ProductVariants] ([VariantId]) ON DELETE NO ACTION
);

CREATE TABLE [OrderStatusHistories] (
    [HistoryId] int NOT NULL IDENTITY,
    [OrderId] int NOT NULL,
    [FromStatusId] int NULL,
    [ToStatusId] int NOT NULL,
    [ChangedBy] int NULL,
    [Note] nvarchar(255) NULL,
    [ChangedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_OrderStatusHistories] PRIMARY KEY ([HistoryId]),
    CONSTRAINT [FK_OrderStatusHistories_OrderStatuses_FromStatusId] FOREIGN KEY ([FromStatusId]) REFERENCES [OrderStatuses] ([StatusId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_OrderStatusHistories_OrderStatuses_ToStatusId] FOREIGN KEY ([ToStatusId]) REFERENCES [OrderStatuses] ([StatusId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_OrderStatusHistories_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([OrderId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_OrderStatusHistories_Users_ChangedBy] FOREIGN KEY ([ChangedBy]) REFERENCES [Users] ([UserId]) ON DELETE NO ACTION
);

CREATE TABLE [Payments] (
    [PaymentId] int NOT NULL IDENTITY,
    [OrderId] int NOT NULL,
    [PaymentMethodId] int NOT NULL,
    [Amount] decimal(18,2) NOT NULL,
    [ReceivedAmount] decimal(18,2) NULL,
    [PaymentStatus] nvarchar(20) NOT NULL,
    [TransactionCode] nvarchar(100) NULL,
    [ExpiresAt] datetime2 NULL,
    [PaidAt] datetime2 NULL,
    [RefundedAt] datetime2 NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_Payments] PRIMARY KEY ([PaymentId]),
    CONSTRAINT [CK_Payments_Amount] CHECK ([Amount] >= 0),
    CONSTRAINT [CK_Payments_Expired_ExpiresAt] CHECK (([PaymentStatus] = 'Expired' AND [ExpiresAt] IS NOT NULL) OR ([PaymentStatus] <> 'Expired')),
    CONSTRAINT [CK_Payments_ExpiresAt_CreatedAt] CHECK ([ExpiresAt] IS NULL OR [ExpiresAt] > [CreatedAt]),
    CONSTRAINT [CK_Payments_ReceivedAmount] CHECK ([ReceivedAmount] IS NULL OR [ReceivedAmount] >= 0),
    CONSTRAINT [CK_Payments_Refunded] CHECK (([PaymentStatus] = 'Refunded' AND [RefundedAt] IS NOT NULL) OR ([PaymentStatus] <> 'Refunded' AND [RefundedAt] IS NULL)),
    CONSTRAINT [CK_Payments_RefundedAt_PaidAt] CHECK ([RefundedAt] IS NULL OR [PaidAt] IS NULL OR [RefundedAt] >= [PaidAt]),
    CONSTRAINT [CK_Payments_Status] CHECK ([PaymentStatus] IN ('Pending', 'Paid', 'Failed', 'Expired', 'RefundPending', 'Refunded')),
    CONSTRAINT [CK_Payments_Status_ReceivedAmount_PaidAt] CHECK (([PaymentStatus] IN ('Paid', 'RefundPending', 'Refunded') AND [ReceivedAmount] = [Amount] AND [PaidAt] IS NOT NULL) OR ([PaymentStatus] IN ('Pending', 'Failed', 'Expired') AND [ReceivedAmount] IS NULL AND [PaidAt] IS NULL)),
    CONSTRAINT [CK_Payments_StrictExpiration] CHECK (([PaymentStatus] IN ('Paid', 'RefundPending', 'Refunded') AND [ExpiresAt] IS NOT NULL AND [PaidAt] < [ExpiresAt]) OR ([PaymentStatus] NOT IN ('Paid', 'RefundPending', 'Refunded') OR [ExpiresAt] IS NULL)),
    CONSTRAINT [CK_Payments_UpdatedAt] CHECK ([UpdatedAt] >= [CreatedAt]),
    CONSTRAINT [FK_Payments_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([OrderId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_Payments_PaymentMethods_PaymentMethodId] FOREIGN KEY ([PaymentMethodId]) REFERENCES [PaymentMethods] ([PaymentMethodId]) ON DELETE NO ACTION
);

CREATE TABLE [InventoryTransactions] (
    [TransactionId] bigint NOT NULL IDENTITY,
    [VariantId] int NOT NULL,
    [TransactionType] nvarchar(20) NOT NULL,
    [QuantityChange] int NOT NULL,
    [QuantityAfter] int NOT NULL,
    [OrderItemId] int NULL,
    [Note] nvarchar(255) NULL,
    [CreatedBy] int NULL,
    [CreatedAt] datetime2 NOT NULL,
    CONSTRAINT [PK_InventoryTransactions] PRIMARY KEY ([TransactionId]),
    CONSTRAINT [CK_InventoryTransactions_QuantityAfter] CHECK ([QuantityAfter] >= 0),
    CONSTRAINT [CK_InventoryTransactions_QuantityChange] CHECK ([QuantityChange] <> 0),
    CONSTRAINT [CK_InventoryTransactions_Type] CHECK ([TransactionType] IN ('Import', 'Sale', 'OrderRestock', 'Adjustment')),
    CONSTRAINT [CK_InventoryTransactions_TypeRules] CHECK (([TransactionType] = 'Sale' AND [QuantityChange] < 0 AND [OrderItemId] IS NOT NULL) OR ([TransactionType] = 'OrderRestock' AND [QuantityChange] > 0 AND [OrderItemId] IS NOT NULL) OR ([TransactionType] = 'Import' AND [QuantityChange] > 0 AND [OrderItemId] IS NULL) OR ([TransactionType] = 'Adjustment' AND [OrderItemId] IS NULL)),
    CONSTRAINT [FK_InventoryTransactions_OrderItems_OrderItemId] FOREIGN KEY ([OrderItemId]) REFERENCES [OrderItems] ([OrderItemId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_InventoryTransactions_ProductVariants_VariantId] FOREIGN KEY ([VariantId]) REFERENCES [ProductVariants] ([VariantId]) ON DELETE NO ACTION,
    CONSTRAINT [FK_InventoryTransactions_Users_CreatedBy] FOREIGN KEY ([CreatedBy]) REFERENCES [Users] ([UserId]) ON DELETE NO ACTION
);

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'StatusId', N'IsFinal', N'SortOrder', N'StatusCode', N'StatusName') AND [object_id] = OBJECT_ID(N'[OrderStatuses]'))
    SET IDENTITY_INSERT [OrderStatuses] ON;
INSERT INTO [OrderStatuses] ([StatusId], [IsFinal], [SortOrder], [StatusCode], [StatusName])
VALUES (1, CAST(0 AS bit), 1, N'PENDING', N'Chờ xác nhận'),
(2, CAST(0 AS bit), 2, N'CONFIRMED', N'Đã xác nhận'),
(3, CAST(0 AS bit), 3, N'SHIPPING', N'Đang giao hàng'),
(4, CAST(1 AS bit), 4, N'DELIVERED', N'Giao thành công'),
(5, CAST(1 AS bit), 5, N'CANCELLED', N'Đã hủy');
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'StatusId', N'IsFinal', N'SortOrder', N'StatusCode', N'StatusName') AND [object_id] = OBJECT_ID(N'[OrderStatuses]'))
    SET IDENTITY_INSERT [OrderStatuses] OFF;

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'PaymentMethodId', N'CreatedAt', N'Description', N'IsActive', N'MethodCode', N'MethodName', N'UpdatedAt') AND [object_id] = OBJECT_ID(N'[PaymentMethods]'))
    SET IDENTITY_INSERT [PaymentMethods] ON;
INSERT INTO [PaymentMethods] ([PaymentMethodId], [CreatedAt], [Description], [IsActive], [MethodCode], [MethodName], [UpdatedAt])
VALUES (1, '2026-01-01T00:00:00.0000000Z', N'Thanh toán bằng tiền mặt khi nhận hàng', CAST(1 AS bit), N'COD', N'Thanh toán khi nhận hàng', '2026-01-01T00:00:00.0000000Z'),
(2, '2026-01-01T00:00:00.0000000Z', N'Chuyển khoản qua tài khoản ngân hàng', CAST(1 AS bit), N'BANK_TRANSFER', N'Chuyển khoản ngân hàng', '2026-01-01T00:00:00.0000000Z'),
(3, '2026-01-01T00:00:00.0000000Z', N'Thanh toán qua ví/cổng VNPAY (giai đoạn sau)', CAST(0 AS bit), N'VNPAY', N'Ví/Cổng VNPay', '2026-01-01T00:00:00.0000000Z'),
(4, '2026-01-01T00:00:00.0000000Z', N'Thanh toán qua ví MoMo (giai đoạn sau)', CAST(0 AS bit), N'MOMO', N'Ví MoMo', '2026-01-01T00:00:00.0000000Z');
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'PaymentMethodId', N'CreatedAt', N'Description', N'IsActive', N'MethodCode', N'MethodName', N'UpdatedAt') AND [object_id] = OBJECT_ID(N'[PaymentMethods]'))
    SET IDENTITY_INSERT [PaymentMethods] OFF;

IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'RoleId', N'RoleName') AND [object_id] = OBJECT_ID(N'[Roles]'))
    SET IDENTITY_INSERT [Roles] ON;
INSERT INTO [Roles] ([RoleId], [RoleName])
VALUES (1, N'Admin'),
(2, N'Customer');
IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'RoleId', N'RoleName') AND [object_id] = OBJECT_ID(N'[Roles]'))
    SET IDENTITY_INSERT [Roles] OFF;

CREATE UNIQUE INDEX [UQ_Brands_BrandName] ON [Brands] ([BrandName]);

CREATE UNIQUE INDEX [UQ_Brands_Slug] ON [Brands] ([Slug]);

CREATE INDEX [IX_CartItems_VariantId] ON [CartItems] ([VariantId]);

CREATE UNIQUE INDEX [UQ_CartItems_Cart_Variant] ON [CartItems] ([CartId], [VariantId]);

CREATE UNIQUE INDEX [UQ_Carts_UserId] ON [Carts] ([UserId]);

CREATE INDEX [IX_Categories_ParentCategoryId] ON [Categories] ([ParentCategoryId]);

CREATE UNIQUE INDEX [UQ_Categories_Slug] ON [Categories] ([Slug]);

CREATE UNIQUE INDEX [UQ_Colors_ColorName] ON [Colors] ([ColorName]);

CREATE UNIQUE INDEX [UQ_Inventories_VariantId] ON [Inventories] ([VariantId]);

CREATE INDEX [IX_InventoryTransactions_CreatedBy] ON [InventoryTransactions] ([CreatedBy]);

CREATE INDEX [IX_InventoryTransactions_Variant_CreatedAt] ON [InventoryTransactions] ([VariantId], [CreatedAt]);

CREATE UNIQUE INDEX [UX_InventoryTransactions_OrderItem_Type] ON [InventoryTransactions] ([OrderItemId], [TransactionType]) WHERE [OrderItemId] IS NOT NULL;

CREATE INDEX [IX_OrderItems_ImageUrl] ON [OrderItems] ([ImageUrl]) WHERE [ImageUrl] IS NOT NULL;

CREATE INDEX [IX_OrderItems_OrderId] ON [OrderItems] ([OrderId]);

CREATE INDEX [IX_OrderItems_VariantId] ON [OrderItems] ([VariantId]);

CREATE INDEX [IX_Orders_DeliveredAt_Revenue] ON [Orders] ([DeliveredAt]) INCLUDE ([Subtotal], [ShippingFee], [TotalAmount]) WHERE [DeliveredAt] IS NOT NULL;

CREATE INDEX [IX_Orders_StatusId_CreatedAt] ON [Orders] ([StatusId], [CreatedAt]);

CREATE INDEX [IX_Orders_UserId_CreatedAt] ON [Orders] ([UserId], [CreatedAt]);

CREATE UNIQUE INDEX [UQ_Orders_OrderCode] ON [Orders] ([OrderCode]);

CREATE UNIQUE INDEX [UQ_OrderStatuses_StatusCode] ON [OrderStatuses] ([StatusCode]);

CREATE INDEX [IX_OrderStatusHistories_ChangedBy] ON [OrderStatusHistories] ([ChangedBy]);

CREATE INDEX [IX_OrderStatusHistories_FromStatusId] ON [OrderStatusHistories] ([FromStatusId]);

CREATE INDEX [IX_OrderStatusHistories_OrderId] ON [OrderStatusHistories] ([OrderId]);

CREATE INDEX [IX_OrderStatusHistories_ToStatusId] ON [OrderStatusHistories] ([ToStatusId]);

CREATE UNIQUE INDEX [UQ_PaymentMethods_MethodCode] ON [PaymentMethods] ([MethodCode]);

CREATE INDEX [IX_Payments_OrderId_CreatedAt] ON [Payments] ([OrderId], [CreatedAt]);

CREATE INDEX [IX_Payments_Pending_ExpiresAt] ON [Payments] ([ExpiresAt]) WHERE [PaymentStatus] = 'Pending' AND [ExpiresAt] IS NOT NULL;

CREATE INDEX [IX_Payments_RefundPending] ON [Payments] ([UpdatedAt]) WHERE [PaymentStatus] = 'RefundPending';

CREATE UNIQUE INDEX [UX_Payments_Method_TransactionCode] ON [Payments] ([PaymentMethodId], [TransactionCode]) WHERE [TransactionCode] IS NOT NULL;

CREATE UNIQUE INDEX [UX_Payments_Order_Live] ON [Payments] ([OrderId]) WHERE [PaymentStatus] <> 'Failed' AND [PaymentStatus] <> 'Expired';

CREATE INDEX [IX_ProductImages_ColorId] ON [ProductImages] ([ColorId]);

CREATE UNIQUE INDEX [UQ_ProductImages_ImageUrl] ON [ProductImages] ([ImageUrl]);

CREATE UNIQUE INDEX [UQ_ProductImages_Product_Color_SortOrder] ON [ProductImages] ([ProductId], [ColorId], [SortOrder]);

CREATE UNIQUE INDEX [UX_ProductImages_Primary] ON [ProductImages] ([ProductId]) WHERE [IsPrimary] = 1;

CREATE INDEX [IX_Products_Brand_IsActive] ON [Products] ([BrandId], [IsActive]);

CREATE INDEX [IX_Products_Category_IsActive] ON [Products] ([CategoryId], [IsActive]);

CREATE INDEX [IX_Products_ProductName] ON [Products] ([ProductName]);

CREATE UNIQUE INDEX [UQ_Products_Slug] ON [Products] ([Slug]);

CREATE INDEX [IX_ProductVariants_ColorId] ON [ProductVariants] ([ColorId]);

CREATE INDEX [IX_ProductVariants_Product_IsActive] ON [ProductVariants] ([ProductId], [IsActive]) INCLUDE ([Price]);

CREATE INDEX [IX_ProductVariants_SizeId] ON [ProductVariants] ([SizeId]);

CREATE UNIQUE INDEX [UQ_ProductVariants_Product_Size_Color] ON [ProductVariants] ([ProductId], [SizeId], [ColorId]);

CREATE UNIQUE INDEX [UQ_ProductVariants_Sku] ON [ProductVariants] ([Sku]);

CREATE INDEX [IX_RefreshTokens_UserId] ON [RefreshTokens] ([UserId]);

CREATE UNIQUE INDEX [UQ_RefreshTokens_TokenHash] ON [RefreshTokens] ([TokenHash]);

CREATE UNIQUE INDEX [UQ_Roles_RoleName] ON [Roles] ([RoleName]);

CREATE UNIQUE INDEX [UQ_Sizes_SizeName] ON [Sizes] ([SizeName]);

CREATE UNIQUE INDEX [UX_UserAddresses_User_Default] ON [UserAddresses] ([UserId]) WHERE [IsDefault] = 1;

CREATE INDEX [IX_Users_RoleId] ON [Users] ([RoleId]);

CREATE UNIQUE INDEX [UQ_Users_Email] ON [Users] ([Email]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261120150911_InitialCreate', N'10.0.15');

COMMIT;
GO

