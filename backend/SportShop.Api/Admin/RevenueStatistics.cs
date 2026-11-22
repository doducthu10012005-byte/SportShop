using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SportShop.Api.Data;

namespace SportShop.Api.Admin;

public sealed class RevenueQuery
{
    public string? From { get; init; }
    public string? To { get; init; }
    [DisplayFormat(ConvertEmptyStringToNull = false)] public string? GroupBy { get; init; }
}

public sealed class RevenueStatistics(SportShopDbContext db)
{
    private const string DateFormat = "yyyy-MM-dd";
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
    private sealed record RevenueBucket(DateOnly Period, decimal Subtotal, decimal ShippingFee, decimal TotalAmount, long OrderCount);

    internal async Task<object> Read(RevenueQuery query, CancellationToken ct)
    {
        var from = ReadDate(query.From, "from");
        var to = ReadDate(query.To, "to");
        if (to < from || to.DayNumber - from.DayNumber >= 366)
            throw Catalog.Invalid("to");
        var grouping = query.GroupBy ?? "day";
        if (grouping is not ("day" or "month")) throw Catalog.Invalid("groupBy");

        var startUtc = MidnightUtc(from);
        var endUtc = to == DateOnly.MaxValue ? MidnightUtc(to).AddDays(1) : MidnightUtc(to.AddDays(1));
        var monthly = grouping == "month";
        // Keep the range predicates on the indexed UTC column. Convert only the grouping key.
        var buckets = await db.Database.SqlQuery<RevenueBucket>($"""
            SELECT bucket.Period, SUM(o.Subtotal) AS Subtotal, SUM(o.ShippingFee) AS ShippingFee,
                SUM(o.TotalAmount) AS TotalAmount, COUNT_BIG(*) AS OrderCount
            FROM dbo.Orders AS o
            JOIN dbo.OrderStatuses AS s ON o.StatusId = s.StatusId
            CROSS APPLY (VALUES (CONVERT(date, o.DeliveredAt AT TIME ZONE 'UTC' AT TIME ZONE 'SE Asia Standard Time'))) AS local(LocalDay)
            CROSS APPLY (VALUES (CASE WHEN {monthly} = 1
                THEN DATEFROMPARTS(YEAR(local.LocalDay), MONTH(local.LocalDay), 1)
                ELSE local.LocalDay END)) AS bucket(Period)
            WHERE o.DeliveredAt IS NOT NULL AND o.DeliveredAt >= {startUtc} AND o.DeliveredAt < {endUtc}
                AND s.StatusCode = 'DELIVERED'
            GROUP BY bucket.Period
            ORDER BY bucket.Period
            """).ToListAsync(ct);

        var ordersByStatus = await db.OrderStatuses.AsNoTracking().OrderBy(s => s.SortOrder).ThenBy(s => s.StatusId)
            .Select(s => new
            {
                s.StatusCode,
                count = s.Orders.LongCount(o => o.CreatedAt >= startUtc && o.CreatedAt < endUtc)
            }).ToListAsync(ct);
        var subtotal = buckets.Sum(b => b.Subtotal);
        var totalOrders = buckets.Sum(b => b.OrderCount);
        return new
        {
            from, to,
            summary = new
            {
                subtotal,
                shippingFee = buckets.Sum(b => b.ShippingFee),
                totalAmount = buckets.Sum(b => b.TotalAmount),
                totalOrders,
                averageSubtotal = totalOrders == 0 ? 0 : subtotal / totalOrders
            },
            series = buckets.Select(b => new
            {
                period = b.Period.ToString(monthly ? "yyyy-MM" : DateFormat, CultureInfo.InvariantCulture),
                b.Subtotal, b.ShippingFee, b.TotalAmount, b.OrderCount
            }),
            ordersByStatus
        };
    }

    private static DateOnly ReadDate(string? value, string field) =>
        DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : throw Catalog.Invalid(field);

    private static DateTime MidnightUtc(DateOnly date) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), Vietnam);
}
