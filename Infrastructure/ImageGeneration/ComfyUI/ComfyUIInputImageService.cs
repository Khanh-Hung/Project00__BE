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

        // 1. Resolve HttpContent from Storage, Local File, or Remote URL with SSRF and Size limits
        HttpContent? fileContent = null;
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

                byte[] imageBytes;
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

                if (!ValidateImageMagicBytes(imageBytes, out _))
                {
                    throw new GpuNonTransientException("The reference image content is not a valid supported image format (PNG, JPEG, or WebP).");
                }

                var ext = mime switch
                {
                    "jpeg" or "jpg" => ".jpg",
                    "webp" => ".webp",
                    _ => ".png"
                };
                fileName = $"{Guid.NewGuid():N}{ext}";
                fileContent = new ByteArrayContent(imageBytes);
            }
            else if (referenceImageUrl.Contains("uploads/", StringComparison.OrdinalIgnoreCase) ||
                     (!referenceImageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !referenceImageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                // Path traversal protection
                if (referenceImageUrl.Contains(".."))
                {
                    throw new GpuNonTransientException($"Invalid reference image path containing directory traversal: '{SanitizeUrlForLogging(referenceImageUrl)}'");
                }

                string resolvedPath;
                if (File.Exists(referenceImageUrl))
                {
                    resolvedPath = referenceImageUrl;
                }
                else
                {
                    string relativePath = referenceImageUrl;
                    var uploadIdx = relativePath.IndexOf("uploads/", StringComparison.OrdinalIgnoreCase);
                    if (uploadIdx >= 0)
                    {
                        relativePath = relativePath.Substring(uploadIdx);
                    }
                    else
                    {
                        relativePath = relativePath.TrimStart('/', '\\');
                    }

                    var possiblePath1 = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", relativePath.Replace('/', Path.DirectorySeparatorChar));
                    var possiblePath2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", relativePath.Replace('/', Path.DirectorySeparatorChar));
                    var possiblePath3 = Path.Combine(Directory.GetCurrentDirectory(), relativePath.Replace('/', Path.DirectorySeparatorChar));

                    if (File.Exists(possiblePath1))
                    {
                        resolvedPath = possiblePath1;
                    }
                    else if (File.Exists(possiblePath2))
                    {
                        resolvedPath = possiblePath2;
                    }
                    else if (File.Exists(possiblePath3))
                    {
                        resolvedPath = possiblePath3;
                    }
                    else
                    {
                        throw new GpuNonTransientException($"Reference image could not be resolved from path or URL: '{SanitizeUrlForLogging(referenceImageUrl)}'");
                    }
                }

                var fileInfo = new FileInfo(resolvedPath);
                if (fileInfo.Length > MaxReferenceImageBytes)
                {
                    throw new GpuNonTransientException($"Local reference image exceeds maximum allowed size of {MaxReferenceImageBytes / (1024 * 1024)} MB.");
                }

                var fileStream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var headerBuffer = new byte[16];
                var read = await fileStream.ReadAsync(headerBuffer.AsMemory(0, 16), ct);
                if (!ValidateImageMagicBytes(headerBuffer.AsSpan(0, read), out _))
                {
                    fileStream.Dispose();
                    throw new GpuNonTransientException("The reference image content is not a valid supported image format (PNG, JPEG, or WebP).");
                }

                fileStream.Position = 0;
                fileContent = new StreamContent(fileStream);
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

                await using var responseStream = await responseMessage.Content.ReadAsStreamAsync(ct);
                var memoryStream = new MemoryStream();
                var buffer = new byte[8192];
                int bytesRead;
                long totalBytes = 0;

                while ((bytesRead = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > MaxReferenceImageBytes)
                    {
                        memoryStream.Dispose();
                        throw new GpuNonTransientException($"Remote reference image stream exceeds maximum allowed size of {MaxReferenceImageBytes / (1024 * 1024)} MB.");
                    }
                    await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                }

                if (memoryStream.Length == 0)
                {
                    memoryStream.Dispose();
                    throw new GpuNonTransientException("Remote reference image is empty (0 bytes).");
                }

                // Validate magic bytes from memoryStream without duplicating buffer via ToArray()
                ReadOnlySpan<byte> headerSpan;
                if (memoryStream.TryGetBuffer(out var segment))
                {
                    headerSpan = segment.AsSpan(0, (int)Math.Min(16, memoryStream.Length));
                }
                else
                {
                    memoryStream.Position = 0;
                    var smallHeader = new byte[16];
                    var read = memoryStream.Read(smallHeader, 0, 16);
                    headerSpan = smallHeader.AsSpan(0, read);
                }

                if (!ValidateImageMagicBytes(headerSpan, out _))
                {
                    memoryStream.Dispose();
                    throw new GpuNonTransientException("The reference image content is not a valid supported image format (PNG, JPEG, or WebP).");
                }

                // Stream directly into HttpContent with zero extra buffer duplication
                memoryStream.Position = 0;
                fileContent = new StreamContent(memoryStream);
            }
        }
        catch (GpuNonTransientException)
        {
            fileContent?.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            fileContent?.Dispose();
            _logger.LogError(ex, "Failed to download reference image from '{Url}'", SanitizeUrlForLogging(referenceImageUrl));
            throw new GpuNonTransientException($"Failed to retrieve reference image from '{SanitizeUrlForLogging(referenceImageUrl)}': {ex.Message}", statusCode: null, innerException: ex);
        }

        // 2. Upload image to ComfyUI /upload/image endpoint
        try
        {
            using var form = new MultipartFormDataContent();
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

    public static bool ValidateImageMagicBytes(ReadOnlySpan<byte> bytes, out string detectedFormat)
    {
        detectedFormat = "unknown";
        if (bytes.Length < 4) return false;

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            detectedFormat = "image/png";
            return true;
        }

        // JPEG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            detectedFormat = "image/jpeg";
            return true;
        }

        // WebP: RIFF (bytes 0-3) + WEBP (bytes 8-11)
        if (bytes.Length >= 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            detectedFormat = "image/webp";
            return true;
        }

        return false;
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
