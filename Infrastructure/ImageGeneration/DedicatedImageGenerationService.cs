using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ImageGeneration;

public class DedicatedImageGenerationService : IImageGenerationService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DedicatedImageGenerationService> _logger;
    private readonly PollinationsImageGenerationService _fallbackService;

    public DedicatedImageGenerationService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<DedicatedImageGenerationService> logger,
        PollinationsImageGenerationService fallbackService)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _fallbackService = fallbackService;
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
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
        var serverUrl = _configuration["AiProviders:DedicatedServerUrl"] 
            ?? _configuration["AiProviders:CustomServerUrl"]
            ?? _configuration["AiProviders:FluxApiUrl"];

        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return await _fallbackService.GenerateImageAsync(request, ct);
        }

        try
        {
            var endpoint = serverUrl.TrimEnd('/') + "/generate";
            var payload = new DedicatedServerRequest
            {
                Prompt = request.Prompt,
                NegativePrompt = request.NegativePrompt ?? "lowres, bad anatomy, bad hands, text, error, missing fingers, extra digit, fewer digits, cropped, worst quality, low quality, normal quality, jpeg artifacts, signature, watermark, username, blurry, artist name",
                Width = request.Width > 0 ? request.Width : 1024,
                Height = request.Height > 0 ? request.Height : 1024,
                NumInferenceSteps = 28,
                GuidanceScale = 7.0f,
                ReferenceImage = request.ReferenceImageUrl,
                PreviousSceneImage = request.PreviousSceneImageUrl
            };

            var res = await _httpClient.PostAsJsonAsync(endpoint, payload, ct);
            if (res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadFromJsonAsync<DedicatedServerResponse>(cancellationToken: ct);
                if (body != null && !string.IsNullOrWhiteSpace(body.Image))
                {
                    _logger.LogInformation("Successfully generated image via Dedicated AI server!");
                    return body.Image;
                }
            }
            else
            {
                _logger.LogWarning("Dedicated AI server returned HTTP {Status}. Falling back to default service.", res.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to call Dedicated AI server. Falling back to default image service.");
        }

        return await _fallbackService.GenerateImageAsync(request, ct);
    }

    private sealed class DedicatedServerRequest
    {
        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("negative_prompt")]
        public string? NegativePrompt { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; } = 1024;

        [JsonPropertyName("height")]
        public int Height { get; set; } = 1024;

        [JsonPropertyName("num_inference_steps")]
        public int NumInferenceSteps { get; set; } = 28;

        [JsonPropertyName("guidance_scale")]
        public float GuidanceScale { get; set; } = 7.0f;

        [JsonPropertyName("reference_image")]
        public string? ReferenceImage { get; set; }

        [JsonPropertyName("previous_scene_image")]
        public string? PreviousSceneImage { get; set; }
    }

    private sealed class DedicatedServerResponse
    {
        [JsonPropertyName("image")]
        public string? Image { get; set; }
    }
}
