namespace SportShop.Api.Data.Entities;

public class OrderStatusHistory
{
    public int HistoryId { get; set; }
    public int OrderId { get; set; }
    public int? FromStatusId { get; set; }
    public int ToStatusId { get; set; }
    public int? ChangedBy { get; set; }
    public string? Note { get; set; }
    public DateTime ChangedAt { get; set; }

    public Order Order { get; set; } = null!;
    public OrderStatus? FromStatus { get; set; }
    public OrderStatus ToStatus { get; set; } = null!;
    public User? ChangedByUser { get; set; }
}
