namespace Application.Interfaces;

public interface IImageGenerationService
{
    Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default);
}
