namespace Infrastructure.ImageGeneration.ComfyUI;

/// <summary>
/// Service responsible for resolving reference images from Storage/HTTP and uploading them to ComfyUI /upload/image endpoint.
/// </summary>
public interface IComfyUIInputImageService
{
    Task<string> EnsureImageUploadedAsync(string? referenceImageUrl, CancellationToken ct = default);
}
