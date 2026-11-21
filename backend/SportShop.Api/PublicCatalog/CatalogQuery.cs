using System.ComponentModel.DataAnnotations;

namespace SportShop.Api.PublicCatalog;

public sealed class CatalogQuery : IValidatableObject
{
    public string? Keyword { get; set; }
    [Range(1, int.MaxValue)] public int? CategoryId { get; set; }
    public int[] BrandId { get; set; } = [];
    public int[] SizeId { get; set; } = [];
    public int[] ColorId { get; set; } = [];
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public bool InStock { get; set; }
    public string Sort { get; set; } = "newest";
    [Range(1, int.MaxValue)] public int Page { get; set; } = 1;
    [Range(1, 50)] public int PageSize { get; set; } = 12;

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        foreach (var (ids, name) in new[] { (BrandId, nameof(BrandId)), (SizeId, nameof(SizeId)), (ColorId, nameof(ColorId)) })
            if (ids.Any(id => id <= 0))
                yield return new ValidationResult("Mã phải là số nguyên dương.", [name]);

        if (MinPrice < 0) yield return new ValidationResult("Giá phải không âm.", [nameof(MinPrice)]);
        if (MaxPrice < 0 || MaxPrice < MinPrice)
            yield return new ValidationResult("Khoảng giá không hợp lệ.", [nameof(MaxPrice)]);
        if (Sort is not ("newest" or "price" or "-price" or "name"))
            yield return new ValidationResult("Thứ tự sắp xếp không hợp lệ.", [nameof(Sort)]);
    }
}
