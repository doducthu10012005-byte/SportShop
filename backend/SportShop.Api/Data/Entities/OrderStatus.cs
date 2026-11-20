namespace SportShop.Api.Data.Entities;

public class OrderStatus
{
    public int StatusId { get; set; }
    public string StatusCode { get; set; } = string.Empty;
    public string StatusName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsFinal { get; set; }

    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<OrderStatusHistory> FromStatusHistories { get; set; } = new List<OrderStatusHistory>();
    public ICollection<OrderStatusHistory> ToStatusHistories { get; set; } = new List<OrderStatusHistory>();
}
