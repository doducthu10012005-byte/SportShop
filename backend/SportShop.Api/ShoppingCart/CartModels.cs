using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SportShop.Api.ShoppingCart;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AddCartItem([Range(1, int.MaxValue)] int VariantId, [Range(1, 99)] int Quantity);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetCartQuantity([Range(1, 99)] int Quantity);

internal sealed record CartLine(int CartItemId, int VariantId, int ProductId, string ProductName, string Sku,
    string SizeName, string ColorName, string? ImageUrl, decimal UnitPrice, int Quantity, int AvailableStock)
{
    internal bool Selling { get; init; }
    public bool IsAvailable => Selling && Quantity <= AvailableStock;
    public decimal LineTotal => UnitPrice * Quantity;
}

internal sealed record CartView(int CartId, List<CartLine> Items)
{
    public long TotalItems => Items.Where(i => i.IsAvailable).Sum(i => (long)i.Quantity);
    public decimal Subtotal => Items.Where(i => i.IsAvailable).Sum(i => i.LineTotal);
}
