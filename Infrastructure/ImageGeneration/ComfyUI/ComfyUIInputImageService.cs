using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Application.Exceptions;
using Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ImageGeneration.ComfyUI;

public sealed class ComfyUIInputImageService : IComfyUIInputImageService
{
    public const int MaxReferenceImageBytes = 10 * 1024 * 1024; // 10 MB limit
    private const int MaxBase64EncodedChars = ((MaxReferenceImageBytes + 2) / 3) * 4 + 512; // ~13.3 MB + header buffer

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
            throw new GpuNonTransientException("Reference image URL is missing in the frozen visual snapshot. Visual identity conditioning requires a valid reference image.");
        }

        var serverUrl = _configuration["AiProviders:ComfyUI:ServerUrl"]?.TrimEnd('/') ?? "http://127.0.0.1:8188";

        // 1. Resolve image bytes from Storage, Local File, or Remote URL with SSRF and Size limits
        byte[] imageBytes;
        string fileName = Path.GetFileName(referenceImageUrl.Split('?')[0]);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains('.'))
        {
            fileName = $"{Guid.NewGuid():N}.png";
        }

        try
        {
            if (referenceImageUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                if (referenceImageUrl.Length > MaxBase64EncodedChars)
                {
                    throw new GpuNonTransientException($"Base64 data image URL exceeds maximum allowed size of {MaxReferenceImageBytes / (1024 * 1024)} MB.");
                }

                var match = Regex.Match(referenceImageUrl, @"^data:image\/(?<mime>[a-zA-Z0-9\+\.\-]+);base64,(?<data>[A-Za-z0-9+/=_\-\r\n]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success)
                {
                    var commaIdx = referenceImageUrl.IndexOf(',');
                    if (commaIdx == -1)
                    {
                        throw new GpuNonTransientException("Malformed base64 data image URL: missing comma separator.");
                    }
                    var header = referenceImageUrl[..commaIdx];
                    if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new GpuNonTransientException("Malformed base64 data image URL: missing ';base64' encoding indicator.");
                    }
                    throw new GpuNonTransientException("Invalid base64 characters or format in data image URL.");
                }

                var mime = match.Groups["mime"].Value.ToLowerInvariant();
                var supportedMimes = new[] { "png", "jpeg", "jpg", "webp" };
                if (!supportedMimes.Contains(mime))
                {
                    throw new GpuNonTransientException($"Unsupported data image MIME type 'image/{mime}'. Supported types: png, jpeg, jpg, webp.");
                }

                var base64Data = match.Groups["data"].Value;
                if (string.IsNullOrWhiteSpace(base64Data))
                {
                    throw new GpuNonTransientException("Base64 data image URL payload cannot be empty.");
                }

                try
                {
                    imageBytes = Convert.FromBase64String(base64Data);
                }
                catch (FormatException ex)
                {
                    throw new GpuNonTransientException("Invalid base64 payload in data image URL.", innerException: ex);
                }

                if (imageBytes.Length > MaxReferenceImageBytes)
                {
                    throw new GpuNonTransientException($"Decoded reference image exceeds maximum allowed size of {MaxReferenceImageBytes / (1024 * 1024)} MB.");
                }

                if (imageBytes.Length == 0)
                {
                    throw new GpuNonTransientException("Decoded reference image is empty (0 bytes).");
                }

                var ext = mime switch
                {
                    "jpeg" or "jpg" => ".jpg",
                    "webp" => ".webp",
                    _ => ".png"
                };
                fileName = $"{Guid.NewGuid():N}{ext}";
            }
            else if (referenceImageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                referenceImageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(referenceImageUrl, UriKind.Absolute, out var uri))
                {
                    throw new GpuNonTransientException($"Invalid reference image URI: '{SanitizeUrlForLogging(referenceImageUrl)}'");
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

                using var responseMessage = await _httpClient.GetAsync(referenceImageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!responseMessage.IsSuccessStatusCode)
                {
                    throw new GpuNonTransientException($"Failed to download reference image (HTTP {responseMessage.StatusCode}).");
                }

                if (responseMessage.Content.Headers.ContentLength.HasValue && responseMessage.Content.Headers.ContentLength.Value > MaxReferenceImageBytes)
                {
                    throw new GpuNonTransientException($"Remote reference image exceeds maximum allowed size of {MaxReferenceImageBytes / (1024 * 1024)} MB.");
                }

                imageBytes = await responseMessage.Content.ReadAsByteArrayAsync(ct);

                if (imageBytes.Length > MaxReferenceImageBytes)
                {
                    throw new GpuNonTransientException($"Remote reference image exceeds maximum allowed size of {MaxReferenceImageBytes / (1024 * 1024)} MB.");
                }
            }
            else
            {
                // Path traversal protection
                if (referenceImageUrl.Contains(".."))
                {
                    throw new GpuNonTransientException($"Invalid reference image path containing directory traversal: '{SanitizeUrlForLogging(referenceImageUrl)}'");
                }

                if (File.Exists(referenceImageUrl))
                {
                    var fileInfo = new FileInfo(referenceImageUrl);
                    if (fileInfo.Length > MaxReferenceImageBytes)
                    {
                        throw new GpuNonTransientException($"Local reference image exceeds maximum allowed size of {MaxReferenceImageBytes / (1024 * 1024)} MB.");
                    }
                    imageBytes = await File.ReadAllBytesAsync(referenceImageUrl, ct);
                }
                else
                {
                    // Try resolving relative path from current directory or wwwroot
                    var possiblePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", referenceImageUrl.TrimStart('/', '\\'));
                    if (File.Exists(possiblePath))
                    {
                        var fileInfo = new FileInfo(possiblePath);
                        if (fileInfo.Length > MaxReferenceImageBytes)
                        {
                            throw new GpuNonTransientException($"Local reference image exceeds maximum allowed size of {MaxReferenceImageBytes / (1024 * 1024)} MB.");
                        }
                        imageBytes = await File.ReadAllBytesAsync(possiblePath, ct);
                    }
                    else
                    {
                        throw new GpuNonTransientException($"Reference image could not be resolved from path or URL: '{SanitizeUrlForLogging(referenceImageUrl)}'");
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
            _logger.LogError(ex, "Failed to download reference image from '{Url}'", SanitizeUrlForLogging(referenceImageUrl));
            throw new GpuNonTransientException($"Failed to retrieve reference image from '{SanitizeUrlForLogging(referenceImageUrl)}': {ex.Message}", statusCode: null, innerException: ex);
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

    public static string SanitizeUrlForLogging(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "[empty]";
        if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            var headerEnd = url.IndexOf(';');
            var prefix = headerEnd != -1 ? url[..headerEnd] : "data:image";
            return $"{prefix};base64,[length: {url.Length}]";
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // Remove query string to prevent leaking SAS tokens, signatures, or credentials
            return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        }
        return Path.GetFileName(url);
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
