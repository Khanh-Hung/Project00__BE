namespace Application.Interfaces;

public sealed record ImageGenerationRequest(
    string Prompt,
    int Width = 1024,
    int Height = 1024,
    string? AspectRatio = "16:9",
    string? ReferenceImageUrl = null,
    string? NegativePrompt = null,
    string? PreviousSceneImageUrl = null
);

public interface IImageGenerationService
{
    Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default);
    Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default);
}
