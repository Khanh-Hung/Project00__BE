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

        // 1. Resolve image bytes from Storage, Local File, or Remote URL with SSRF protection
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
                if (!Uri.TryCreate(referenceImageUrl, UriKind.Absolute, out var uri))
                {
                    throw new GpuNonTransientException($"Invalid reference image URI: '{referenceImageUrl}'");
                }

                // SSRF Protection: Deny loopback, private subnets, and cloud metadata addresses
                var host = uri.Host.ToLowerInvariant();
                if (host == "localhost" || host == "127.0.0.1" || host == "169.254.169.254" || host == "0.0.0.0" || host == "::1")
                {
                    _logger.LogWarning("Blocked potential SSRF reference image attempt to host: {Host}", host);
                    throw new GpuNonTransientException($"Access to internal address '{host}' is forbidden for reference images.");
                }

                try
                {
                    var ips = await System.Net.Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
                    if (ips.Length == 0 || ips.Any(IsPrivateOrInternalIp))
                    {
                        _logger.LogWarning("Blocked potential SSRF reference image attempt resolving to private/empty IP for host: {Host}", uri.Host);
                        throw new GpuNonTransientException($"Access to internal IP address for host '{uri.Host}' is forbidden.");
                    }
                }
                catch (GpuNonTransientException) { throw; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "DNS resolution failed for reference image host: {Host}", uri.Host);
                    throw new GpuNonTransientException($"Could not resolve DNS for reference image host '{uri.Host}': {ex.Message}", innerException: ex);
                }

                imageBytes = await _httpClient.GetByteArrayAsync(referenceImageUrl, ct);
            }
            else
            {
                // Path traversal protection
                if (referenceImageUrl.Contains(".."))
                {
                    throw new GpuNonTransientException($"Invalid reference image path containing directory traversal: '{referenceImageUrl}'");
                }

                if (File.Exists(referenceImageUrl))
                {
                    imageBytes = await File.ReadAllBytesAsync(referenceImageUrl, ct);
                }
                else
                {
                    // Try resolving relative path from current directory or wwwroot
                    var possiblePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", referenceImageUrl.TrimStart('/', '\\'));
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

    private static bool IsPrivateOrInternalIp(System.Net.IPAddress ip)
    {
        if (System.Net.IPAddress.IsLoopback(ip) || ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any))
            return true;

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (bytes[0] == 10) return true; // 10.0.0.0/8
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true; // 172.16.0.0/12
            if (bytes[0] == 192 && bytes[1] == 168) return true; // 192.168.0.0/16
            if (bytes[0] == 127) return true; // 127.0.0.0/8
            if (bytes[0] == 169 && bytes[1] == 254) return true; // 169.254.0.0/16
        }
        else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return true;
        }

        return false;
    }
}
