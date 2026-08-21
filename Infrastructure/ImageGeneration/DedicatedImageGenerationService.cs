using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Application.Exceptions;
using Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ImageGeneration;

public class DedicatedImageGenerationService : IImageGenerationService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DedicatedImageGenerationService> _logger;

    public DedicatedImageGenerationService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<DedicatedImageGenerationService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
        _httpClient.Timeout = TimeSpan.FromSeconds(90);
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
            throw new GpuNonTransientException("Dedicated AI server URL is not configured in AiProviders:DedicatedServerUrl.");
        }

        var endpoint = serverUrl.TrimEnd('/') + "/generate";
        
        float defaultIdentityScale = 0.65f;
        float defaultSceneScale = 0.20f;
        if (float.TryParse(_configuration["AiProviders:DefaultIdentityScale"], out var parsedIdScale)) defaultIdentityScale = parsedIdScale;
        if (float.TryParse(_configuration["AiProviders:DefaultSceneScale"], out var parsedSceneScale)) defaultSceneScale = parsedSceneScale;

        var payload = new DedicatedServerRequest
        {
            Prompt = request.Prompt,
            NegativePrompt = request.NegativePrompt ?? "lowres, bad anatomy, bad hands, text, error, missing fingers, extra digit, fewer digits, cropped, worst quality, low quality, normal quality, jpeg artifacts, signature, watermark, username, blurry, artist name",
            Width = request.Width > 0 ? request.Width : 1024,
            Height = request.Height > 0 ? request.Height : 1024,
            NumInferenceSteps = request.Steps ?? 28,
            GuidanceScale = request.GuidanceScale ?? 7.0f,
            ReferenceImage = request.ReferenceImageUrl,
            PreviousSceneImage = request.PreviousSceneImageUrl,
            IdentityScale = request.IdentityScale ?? defaultIdentityScale,
            SceneScale = request.SceneScale ?? defaultSceneScale,
            Seed = request.Seed
        };

        HttpResponseMessage res;
        try
        {
            res = await _httpClient.PostAsJsonAsync(endpoint, payload, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Dedicated AI server request timed out after 90 seconds.");
            throw new GpuTransientException("Dedicated AI server request timed out.", (int)HttpStatusCode.RequestTimeout, ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Network transport error communicating with Dedicated AI server.");
            throw new GpuTransientException($"Network error connecting to Dedicated AI server: {ex.Message}", null, ex);
        }

        if (res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadFromJsonAsync<DedicatedServerResponse>(cancellationToken: ct);
            if (body != null && !string.IsNullOrWhiteSpace(body.Image))
            {
                _logger.LogInformation("Successfully generated image via Dedicated AI server! (IdentityScale={IdScale}, SceneScale={SceneScale})", payload.IdentityScale, payload.SceneScale);
                return body.Image;
            }
            throw new GpuTransientException("Dedicated AI server returned empty or null image response body.", (int)res.StatusCode);
        }

        var statusCode = (int)res.StatusCode;
        var errorBody = await res.Content.ReadAsStringAsync(ct);
        _logger.LogWarning("Dedicated AI server returned HTTP {Status}: {ErrorBody}", statusCode, errorBody);

        // Error Classification: Transient (Retry) vs Non-Transient (Fast-Fail)
        if (statusCode == 408 || statusCode == 429 || statusCode >= 500)
        {
            throw new GpuTransientException($"Dedicated AI server transient error (HTTP {statusCode}): {errorBody}", statusCode);
        }
        else
        {
            // 400 Bad Request, 404 Reference Not Found, 422 Unprocessable Entity
            throw new GpuNonTransientException($"Dedicated AI server non-transient error (HTTP {statusCode}): {errorBody}", statusCode);
        }
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

        [JsonPropertyName("identity_scale")]
        public float IdentityScale { get; set; } = 0.65f;

        [JsonPropertyName("scene_scale")]
        public float SceneScale { get; set; } = 0.20f;

        [JsonPropertyName("seed")]
        public long? Seed { get; set; }
    }

    private sealed class DedicatedServerResponse
    {
        [JsonPropertyName("image")]
        public string? Image { get; set; }
    }
}
