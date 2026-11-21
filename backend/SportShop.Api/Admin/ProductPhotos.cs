using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using SportShop.Api.Data;
using SportShop.Api.Data.Entities;
using SportShop.Api.Middleware;

namespace SportShop.Api.Admin;

public sealed class ProductPhotos(SportShopDbContext db, IWebHostEnvironment env, ILogger<ProductPhotos> logger)
{
    internal const int FileLimit = 2 * 1024 * 1024;
    internal static ApiFailure TooLarge() => new(413, "PAYLOAD_TOO_LARGE", "Ảnh vượt quá 2 MB.");
    private static ApiFailure Unsupported() => new(415, "UNSUPPORTED_MEDIA_TYPE", "Chỉ chấp nhận ảnh JPG, PNG hoặc WEBP.");

    internal async Task<object> Upload(int id, IFormFile file, int? colorId, bool primary, int actor, CancellationToken ct)
    {
        if (file.Length > FileLimit) throw TooLarge();
        if (file.Length == 0) throw Catalog.Invalid("ImageFile");
        await using var input = file.OpenReadStream();
        var header = new byte[Math.Min(12, file.Length)];
        await input.ReadExactlyAsync(header, ct);
        var extension = DetectExtension(header);
        var supplied = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (supplied != extension && !(extension == ".jpg" && supplied == ".jpeg")) throw Unsupported();
        input.Position = 0;
        using var codec = SKCodec.Create(input) ?? throw Catalog.Invalid("ImageFile");
        var format = codec.EncodedFormat;
        var info = codec.Info;
        if (info.Width is < 1 or > 4000 || info.Height is < 1 or > 4000) throw Catalog.Invalid("ImageFile");
        // Encode only decoded pixels. Do not copy EXIF, metadata or trailing input bytes.
        using var bitmap = new SKBitmap(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) throw Catalog.Invalid("ImageFile");
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 90) ?? throw Catalog.Invalid("ImageFile");

        var fileName = Guid.NewGuid().ToString("N") + extension;
        var result = await Catalog.Write(db, async () =>
        {
            if (!await db.Products.AnyAsync(p => p.ProductId == id, ct)) throw Catalog.Missing();
            if (colorId.HasValue && !await db.ProductVariants.AnyAsync(v => v.ProductId == id && v.ColorId == colorId, ct))
                throw Catalog.Invalid("colorId");
            var group = db.ProductImages.Where(i => i.ProductId == id);
            if (await group.CountAsync(ct) >= 10) throw Catalog.Invalid("ImageFile");
            var last = await group.Where(i => i.ColorId == colorId).MaxAsync(i => (int?)i.SortOrder, ct);
            var now = DateTime.UtcNow;
            if (primary)
                await group.Where(i => i.IsPrimary).ExecuteUpdateAsync(s => s.SetProperty(i => i.IsPrimary, false)
                    .SetProperty(i => i.UpdatedAt, now), ct);
            var photo = new ProductImage
            {
                ProductId = id,
                ColorId = colorId,
                IsPrimary = primary,
                ImageUrl = $"/uploads/products/{id}/{fileName}",
                SortOrder = (last ?? -1) + 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.ProductImages.Add(photo);
            await db.SaveChangesAsync(ct);
            var directory = Path.Combine(env.WebRootPath, "uploads", "products", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            await using var output = new FileStream(Path.Combine(directory, fileName), FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, useAsync: true);
            using var bytes = encoded.AsStream();
            await bytes.CopyToAsync(output, ct);
            return new { photo.ImageId, photo.ProductId, photo.ColorId, photo.ImageUrl, photo.IsPrimary, photo.SortOrder };
        }, ct);
        logger.LogInformation("Admin {Actor} uploaded {Name} for product {Product} ({Bytes} bytes) at {Time}",
            actor, fileName, id, file.Length, DateTime.UtcNow);
        return result;
    }

    private static string DetectExtension(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith((ReadOnlySpan<byte>)[0xff, 0xd8, 0xff])) return ".jpg";
        if (header.StartsWith((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10])) return ".png";
        if (header.Length == 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..].SequenceEqual("WEBP"u8)) return ".webp";
        throw Unsupported();
    }

    internal Task Delete(int id, int imageId, CancellationToken ct) => Catalog.Write(db, async () =>
    {
        var photo = await db.ProductImages.SingleOrDefaultAsync(i => i.ProductId == id && i.ImageId == imageId, ct)
            ?? throw Catalog.Missing();
        db.ProductImages.Remove(photo);
        return true;
    }, ct);
}
