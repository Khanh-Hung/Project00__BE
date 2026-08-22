using System.Net.Http.Headers;
using System.Text.Json;
using Application.Exceptions;
using Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ImageGeneration.ComfyUI;

public sealed class ComfyUIInputImageService : IComfyUIInputImageService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ComfyUIInputImageService> _logger;

    public ComfyUIInputImageService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<ComfyUIInputImageService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> EnsureImageUploadedAsync(string? referenceImageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(referenceImageUrl))
        {
            throw new GpuNonTransientException("Reference image URL is missing. Visual identity conditioning requires a valid reference image.");
        }

        var serverUrl = _configuration["AiProviders:ComfyUI:ServerUrl"]?.TrimEnd('/') ?? "http://127.0.0.1:8188";

        // 1. Resolve image bytes from Storage, Local File, or Remote URL
        byte[] imageBytes;
        string fileName = Path.GetFileName(referenceImageUrl.Split('?')[0]);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains('.'))
        {
            fileName = $"{Guid.NewGuid():N}.png";
        }

        try
        {
            if (referenceImageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                referenceImageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                imageBytes = await _httpClient.GetByteArrayAsync(referenceImageUrl, ct);
            }
            else if (File.Exists(referenceImageUrl))
            {
                imageBytes = await File.ReadAllBytesAsync(referenceImageUrl, ct);
            }
            else
            {
                // Try resolving relative path from current directory or wwwroot
                var possiblePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", referenceImageUrl.TrimStart('/'));
                if (File.Exists(possiblePath))
                {
                    imageBytes = await File.ReadAllBytesAsync(possiblePath, ct);
                }
                else
                {
                    throw new GpuNonTransientException($"Reference image could not be resolved from path or URL: '{referenceImageUrl}'");
                }
            }
        }
        catch (GpuNonTransientException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to download reference image from '{Url}'", referenceImageUrl);
            throw new GpuNonTransientException($"Failed to retrieve reference image from '{referenceImageUrl}': {ex.Message}", statusCode: null, innerException: ex);
        }

        // 2. Upload image to ComfyUI /upload/image endpoint
        try
        {
            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(imageBytes);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(GetContentType(fileName));
            form.Add(fileContent, "image", fileName);
            form.Add(new StringContent("true"), "overwrite");
            form.Add(new StringContent("input"), "type");

            var response = await _httpClient.PostAsync($"{serverUrl}/upload/image", form, ct);
            var responseContent = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to upload reference image to ComfyUI: Status={Status}, Body={Body}", response.StatusCode, responseContent);
                throw new GpuTransientException($"Failed to upload reference image to ComfyUI (HTTP {response.StatusCode}): {responseContent}");
            }

            using var doc = JsonDocument.Parse(responseContent);
            if (doc.RootElement.TryGetProperty("name", out var nameProp))
            {
                var uploadedName = nameProp.GetString();
                if (!string.IsNullOrWhiteSpace(uploadedName))
                {
                    _logger.LogInformation("Reference image successfully uploaded to ComfyUI: '{UploadedName}'", uploadedName);
                    return uploadedName;
                }
            }

            return fileName;
        }
        catch (GpuTransientException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error uploading reference image to ComfyUI at '{ServerUrl}'", serverUrl);
            throw new GpuTransientException($"Failed to communicate with ComfyUI upload endpoint: {ex.Message}", statusCode: null, innerException: ex);
        }
    }

    private static string GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }
}
