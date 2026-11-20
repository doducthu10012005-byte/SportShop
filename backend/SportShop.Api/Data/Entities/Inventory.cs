namespace SportShop.Api.Data.Entities;

public class Inventory
{
    public int InventoryId { get; set; }
    public int VariantId { get; set; }
    public int QuantityOnHand { get; set; }
    public int LowStockThreshold { get; set; } = 5;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = null!;

    public ProductVariant Variant { get; set; } = null!;
}
