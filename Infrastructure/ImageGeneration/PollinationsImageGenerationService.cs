using Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ImageGeneration;

public sealed class PollinationsImageGenerationService : IImageGenerationService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PollinationsImageGenerationService> _logger;

    public PollinationsImageGenerationService(
        HttpClient httpClient,
        ILogger<PollinationsImageGenerationService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public Task<string> GenerateImageAsync(
        string prompt,
        int width = 512,
        int height = 512,
        CancellationToken ct = default)
    {
        return GenerateImageAsync(new ImageGenerationRequest(prompt, width, height), ct);
    }

    public async Task<string> GenerateImageAsync(
        ImageGenerationRequest request,
        CancellationToken ct = default)
    {
        var finalPrompt = $"masterpiece, best quality, otome isekai manhwa webtoon art style, roxana anime aesthetic, {request.Prompt.Trim()}, large bright luminous sparkling anime eyes, detailed eyelashes, charming gentle smile, cute small lips, sharp clean lineart, vivid anime coloring, 8k";
        var negativePrompt = "sleepy eyes, half-closed eyes, narrow eyes, squinting, heavy makeup, duck face, pout, 3d render, realistic, photorealistic, plastic skin, doll, uncanny, creepy, alien face, deformed, bad eyes, bad mouth, blurry, extra fingers, mutated anatomy";
        var randomSeed = Random.Shared.Next(1, 99999999);
        var imageUrl = $"https://image.pollinations.ai/prompt/{Uri.EscapeDataString(finalPrompt)}?width={request.Width}&height={request.Height}&nologo=true&enhance=false&negative_prompt={Uri.EscapeDataString(negativePrompt)}&seed={randomSeed}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            var response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes != null && bytes.Length > 1000)
                {
                    var base64 = Convert.ToBase64String(bytes);
                    return $"data:image/jpeg;base64,{base64}";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download image from Pollinations. Returning direct CDN URL as fallback.");
        }

        return imageUrl;
    }
}
