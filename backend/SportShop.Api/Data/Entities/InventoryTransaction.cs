namespace SportShop.Api.Data.Entities;

public class InventoryTransaction
{
    public long TransactionId { get; set; }
    public int VariantId { get; set; }
    public string TransactionType { get; set; } = string.Empty;
    public int QuantityChange { get; set; }
    public int QuantityAfter { get; set; }
    public int? OrderItemId { get; set; }
    public string? Note { get; set; }
    public int? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    public ProductVariant Variant { get; set; } = null!;
    public OrderItem? OrderItem { get; set; }
    public User? User { get; set; }
}
