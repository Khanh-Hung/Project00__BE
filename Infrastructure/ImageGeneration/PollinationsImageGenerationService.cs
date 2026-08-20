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
        var finalPrompt = $"{request.Prompt.Trim()}, masterpiece, best quality, ultra-detailed, sharp focus, 8k";
        var randomSeed = Random.Shared.Next(1, 99999999);
        var imageUrl = $"https://image.pollinations.ai/prompt/{Uri.EscapeDataString(finalPrompt)}?width={request.Width}&height={request.Height}&nologo=true&seed={randomSeed}";

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
