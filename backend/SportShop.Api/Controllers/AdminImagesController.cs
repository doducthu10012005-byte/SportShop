using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using SportShop.Api.Admin;
using SportShop.Api.Middleware;

namespace SportShop.Api.Controllers;

// Read the form before MVC model binding so transport limits keep their HTTP 413 envelope.
[AttributeUsage(AttributeTargets.Method)]
internal sealed class ProductImageFormAttribute : Attribute, IAsyncResourceFilter
{
    internal const int BodyLimit = ProductPhotos.FileLimit + 64 * 1024;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (http.Request.ContentLength > BodyLimit) throw ProductPhotos.TooLarge();
        if (!http.Request.HasFormContentType)
            throw new ApiFailure(415, "UNSUPPORTED_MEDIA_TYPE", "Yêu cầu phải dùng multipart/form-data.");
        try
        {
            await http.Request.ReadFormAsync(http.RequestAborted);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413) { throw ProductPhotos.TooLarge(); }
        catch (InvalidDataException) { throw Catalog.Invalid("ImageFile"); }
        await next();
    }
}

[Route("api/v1/admin/products/{productId:int}/images")]
public sealed class AdminImagesController(ProductPhotos photos) : AdminController
{
    [HttpPost, EnableRateLimiting("image-upload"), ProductImageForm, RequestSizeLimit(ProductImageFormAttribute.BodyLimit)]
    public async Task<IActionResult> Upload(int productId, [FromForm] IFormFile imageFile,
        [FromForm] int? colorId, [FromForm] bool isPrimary = false, CancellationToken ct = default)
    {
        if (Request.Form.Files.Count != 1) throw Catalog.Invalid("ImageFile");
        return Success(await photos.Upload(productId, imageFile, colorId, isPrimary, ActorId, ct), 201, "Đã tải ảnh lên.");
    }

    [HttpDelete("{imageId:int}")]
    public async Task<IActionResult> Delete(int productId, int imageId, CancellationToken ct)
    {
        await photos.Delete(productId, imageId, ct);
        return Success(message: "Đã xóa ảnh.");
    }
}
