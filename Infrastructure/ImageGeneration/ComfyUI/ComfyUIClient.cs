using System.Net.Http.Json;
using System.Text.Json;
using Application.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ImageGeneration.ComfyUI;

public sealed class ComfyUIClient : IComfyUIClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ComfyUIClient> _logger;

    public ComfyUIClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<ComfyUIClient> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private string GetServerUrl() => _configuration["AiProviders:ComfyUI:ServerUrl"]?.TrimEnd('/') ?? "http://127.0.0.1:8188";

    public async Task<string> QueuePromptAsync(Dictionary<string, object> promptGraph, CancellationToken ct = default)
    {
        var serverUrl = GetServerUrl();
        var promptPayload = new { prompt = promptGraph };

        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{serverUrl}/prompt", promptPayload, ct);
            var responseContent = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                _logger.LogWarning("ComfyUI /prompt returned HTTP {StatusCode}: {Body}", statusCode, responseContent);
                if (statusCode >= 500 || statusCode == 408 || statusCode == 429)
                {
                    throw new GpuTransientException($"ComfyUI transient error (HTTP {statusCode}): {responseContent}", statusCode);
                }
                else
                {
                    throw new GpuNonTransientException($"ComfyUI non-transient error (HTTP {statusCode}): {responseContent}", statusCode);
                }
            }

            using var doc = JsonDocument.Parse(responseContent);
            if (doc.RootElement.TryGetProperty("prompt_id", out var promptIdProp))
            {
                return promptIdProp.GetString() ?? throw new GpuTransientException("ComfyUI returned null prompt_id.");
            }
            throw new GpuTransientException("ComfyUI response did not contain prompt_id.");
        }
        catch (GpuTransientException) { throw; }
        catch (GpuNonTransientException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to connect to ComfyUI server at {ServerUrl}", serverUrl);
            throw new GpuTransientException($"Cannot connect to ComfyUI server at '{serverUrl}': {ex.Message}", statusCode: null, innerException: ex);
        }
    }

    public async Task<ComfyUIHistoryResult?> GetHistoryAsync(string promptId, CancellationToken ct = default)
    {
        var serverUrl = GetServerUrl();

        try
        {
            var historyRes = await _httpClient.GetAsync($"{serverUrl}/history/{promptId}", ct);
            if (!historyRes.IsSuccessStatusCode)
            {
                var statusCode = (int)historyRes.StatusCode;
                if (historyRes.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // 404: Prompt is still queued / processing and not yet in history table
                    return null;
                }
                if (statusCode >= 500 || statusCode == 408 || statusCode == 429)
                {
                    throw new GpuTransientException($"ComfyUI server error (HTTP {statusCode}) while querying history for PromptId={promptId}", statusCode);
                }
                if (statusCode == 401 || statusCode == 403)
                {
                    throw new GpuNonTransientException($"ComfyUI authentication failed (HTTP {statusCode})", statusCode);
                }
                
                _logger.LogWarning("ComfyUI /history returned unexpected HTTP {StatusCode} for PromptId={PromptId}", statusCode, promptId);
                return null;
            }

            var historyJson = await historyRes.Content.ReadAsStringAsync(ct);
            using var historyDoc = JsonDocument.Parse(historyJson);

            if (!historyDoc.RootElement.TryGetProperty(promptId, out var item))
            {
                return null;
            }

            if (item.TryGetProperty("status", out var statusProp))
            {
                if (statusProp.TryGetProperty("status_str", out var statusStr) && statusStr.GetString() == "error")
                {
                    var errorDetails = statusProp.TryGetProperty("messages", out var msgs) ? msgs.ToString() : "Unknown ComfyUI execution error.";
                    return new ComfyUIHistoryResult(promptId, false, errorDetails, Array.Empty<ComfyUIHistoryOutputImage>());
                }
            }

            var outputImages = new List<ComfyUIHistoryOutputImage>();
            if (item.TryGetProperty("outputs", out var outputs))
            {
                foreach (var outputNode in outputs.EnumerateObject())
                {
                    if (outputNode.Value.TryGetProperty("images", out var images) && images.GetArrayLength() > 0)
                    {
                        foreach (var img in images.EnumerateArray())
                        {
                            var fn = img.GetProperty("filename").GetString();
                            var sf = img.TryGetProperty("subfolder", out var sfProp) ? sfProp.GetString() : "";
                            var tp = img.TryGetProperty("type", out var tpProp) ? tpProp.GetString() : "output";
                            if (!string.IsNullOrWhiteSpace(fn))
                            {
                                outputImages.Add(new ComfyUIHistoryOutputImage(fn, sf ?? "", tp ?? "output"));
                            }
                        }
                    }
                }
            }

            return new ComfyUIHistoryResult(promptId, true, null, outputImages);
        }
        catch (GpuTransientException) { throw; }
        catch (GpuNonTransientException) { throw; }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Network drop while querying ComfyUI history for PromptId={PromptId}", promptId);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON parse error while parsing ComfyUI history for PromptId={PromptId}", promptId);
            return null;
        }
    }

    public async Task<byte[]> DownloadImageAsync(string filename, string? subfolder = null, string? type = "output", CancellationToken ct = default)
    {
        var serverUrl = GetServerUrl();
        var viewUrl = $"{serverUrl}/view?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder ?? "")}&type={Uri.EscapeDataString(type ?? "output")}";

        try
        {
            return await _httpClient.GetByteArrayAsync(viewUrl, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to download rendered image from ComfyUI /view URL: {ViewUrl}", viewUrl);
            throw new GpuTransientException($"Failed to download rendered image from ComfyUI /view: {ex.Message}", statusCode: null, innerException: ex);
        }
    }
}
