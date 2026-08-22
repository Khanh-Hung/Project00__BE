using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Application.Exceptions;
using Application.Interfaces;
using Infrastructure.ImageGeneration.ComfyUI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ImageGeneration;

public sealed class ComfyUIImageGenerationService : IImageGenerationService
{
    private readonly HttpClient _httpClient;
    private readonly IStorageService _storageService;
    private readonly IComfyUIInputImageService _inputImageService;
    private readonly IEnumerable<IComfyUIWorkflowBuilder> _workflowBuilders;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ComfyUIImageGenerationService> _logger;

    public ComfyUIImageGenerationService(
        HttpClient httpClient,
        IStorageService storageService,
        IComfyUIInputImageService inputImageService,
        IEnumerable<IComfyUIWorkflowBuilder> workflowBuilders,
        IConfiguration configuration,
        ILogger<ComfyUIImageGenerationService> logger)
    {
        _httpClient = httpClient;
        _storageService = storageService;
        _inputImageService = inputImageService;
        _workflowBuilders = workflowBuilders;
        _configuration = configuration;
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
        var result = await GenerateImageWithResultAsync(request, ct);
        return result.ImageUrl;
    }

    public async Task<ImageGenerationResult> GenerateImageWithResultAsync(
        ImageGenerationRequest request,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var serverUrl = _configuration["AiProviders:ComfyUI:ServerUrl"]?.TrimEnd('/') ?? "http://127.0.0.1:8188";

        int pollIntervalMs = 500;
        int timeoutSeconds = 120;
        if (int.TryParse(_configuration["AiProviders:ComfyUI:PollIntervalMs"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInterval)) pollIntervalMs = parsedInterval;
        if (int.TryParse(_configuration["AiProviders:ComfyUI:TimeoutSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTimeout)) timeoutSeconds = parsedTimeout;

        // 1. Ensure Reference Image is uploaded to ComfyUI /upload/image (throws if invalid/missing)
        var resolvedReferenceImageName = await _inputImageService.EnsureImageUploadedAsync(request.ReferenceImageUrl, ct);

        // 2. Select Workflow Builder by (Workflow, WorkflowVersion)
        var targetWorkflow = request.Workflow ?? "VisualIdentity";
        var targetVersion = request.WorkflowVersion;

        var builder = _workflowBuilders.FirstOrDefault(b =>
            string.Equals(b.WorkflowName, targetWorkflow, StringComparison.OrdinalIgnoreCase) &&
            b.WorkflowVersion == targetVersion)
            ?? _workflowBuilders.FirstOrDefault(b => string.Equals(b.WorkflowName, "VisualIdentity", StringComparison.OrdinalIgnoreCase))
            ?? throw new GpuNonTransientException($"No ComfyUI workflow builder found for Workflow='{targetWorkflow}', Version={targetVersion}");

        var workflow = builder.BuildWorkflow(request, resolvedReferenceImageName);
        var promptPayload = new { prompt = workflow };

        // 3. Post Prompt to ComfyUI
        string promptId;
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
                promptId = promptIdProp.GetString() ?? throw new GpuTransientException("ComfyUI returned null prompt_id.");
            }
            else
            {
                throw new GpuTransientException("ComfyUI response did not contain prompt_id.");
            }
        }
        catch (GpuTransientException) { throw; }
        catch (GpuNonTransientException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to connect to ComfyUI server at {ServerUrl}", serverUrl);
            throw new GpuTransientException($"Cannot connect to ComfyUI server at '{serverUrl}': {ex.Message}", statusCode: null, innerException: ex);
        }

        _logger.LogInformation("ComfyUI prompt enqueued with PromptId={PromptId}. Polling for completion...", promptId);

        // 4. Poll /history/{prompt_id} until completed or timeout
        var startTime = DateTime.UtcNow;
        var timeoutLimit = TimeSpan.FromSeconds(timeoutSeconds);
        string? outputFilename = null;
        string? outputSubfolder = null;
        string? outputType = null;

        while (DateTime.UtcNow - startTime < timeoutLimit)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(pollIntervalMs, ct);

            try
            {
                var historyRes = await _httpClient.GetAsync($"{serverUrl}/history/{promptId}", ct);
                if (!historyRes.IsSuccessStatusCode)
                {
                    continue;
                }

                var historyJson = await historyRes.Content.ReadAsStringAsync(ct);
                using var historyDoc = JsonDocument.Parse(historyJson);

                if (historyDoc.RootElement.TryGetProperty(promptId, out var item))
                {
                    if (item.TryGetProperty("status", out var statusProp))
                    {
                        if (statusProp.TryGetProperty("status_str", out var statusStr) && statusStr.GetString() == "error")
                        {
                            var errorDetails = statusProp.TryGetProperty("messages", out var msgs) ? msgs.ToString() : "Unknown ComfyUI execution error.";
                            _logger.LogError("ComfyUI execution failed for PromptId={PromptId}: {Error}", promptId, errorDetails);
                            throw new GpuNonTransientException($"ComfyUI workflow execution failed: {errorDetails}");
                        }
                    }

                    if (item.TryGetProperty("outputs", out var outputs))
                    {
                        foreach (var outputNode in outputs.EnumerateObject())
                        {
                            if (outputNode.Value.TryGetProperty("images", out var images) && images.GetArrayLength() > 0)
                            {
                                var firstImg = images[0];
                                outputFilename = firstImg.GetProperty("filename").GetString();
                                outputSubfolder = firstImg.TryGetProperty("subfolder", out var sf) ? sf.GetString() : "";
                                outputType = firstImg.TryGetProperty("type", out var tp) ? tp.GetString() : "output";
                                break;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(outputFilename))
                        {
                            break;
                        }
                    }
                }
            }
            catch (GpuNonTransientException) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Transient poll error while checking history for PromptId={PromptId}", promptId);
            }
        }

        if (string.IsNullOrWhiteSpace(outputFilename))
        {
            throw new GpuTransientException($"ComfyUI generation timed out after {timeoutSeconds}s for PromptId={promptId}.");
        }

        // 5. Download rendered image via ComfyUI /view API
        var viewUrl = $"{serverUrl}/view?filename={Uri.EscapeDataString(outputFilename)}&subfolder={Uri.EscapeDataString(outputSubfolder ?? "")}&type={Uri.EscapeDataString(outputType ?? "output")}";
        byte[] imageBytes;
        try
        {
            imageBytes = await _httpClient.GetByteArrayAsync(viewUrl, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to download rendered image from ComfyUI /view URL: {ViewUrl}", viewUrl);
            throw new GpuTransientException($"Failed to download rendered image from ComfyUI /view: {ex.Message}", statusCode: null, innerException: ex);
        }

        // 6. Save image to authoritative IStorageService
        var storedUrl = await _storageService.SaveImageAsync(
            imageBytes: imageBytes,
            fileName: outputFilename,
            contentType: "image/png",
            ct: ct
        );

        stopwatch.Stop();
        var seedUsed = request.Seed ?? 0;
        var metadataJson = JsonSerializer.Serialize(new
        {
            PromptId = promptId,
            DurationMs = stopwatch.ElapsedMilliseconds,
            Seed = seedUsed,
            Model = request.Model ?? "meinamix_meinaV11.safetensors",
            Workflow = builder.WorkflowName,
            WorkflowVersion = builder.WorkflowVersion,
            IPAdapterWeight = request.IPAdapterWeight ?? 0.45f,
            IPAdapterEndAt = request.IPAdapterEndAt ?? 0.70f,
            Width = request.Width,
            Height = request.Height,
            Steps = request.Steps,
            Cfg = request.GuidanceScale,
            Sampler = request.Sampler,
            Scheduler = request.Scheduler
        });

        _logger.LogInformation("ComfyUI Image Generation completed successfully: StoredUrl={Url}, Duration={Duration}ms, PromptId={PromptId}",
            storedUrl, stopwatch.ElapsedMilliseconds, promptId);

        return new ImageGenerationResult(
            ImageUrl: storedUrl,
            Provider: "ComfyUI",
            ProviderJobId: promptId,
            DurationMs: stopwatch.ElapsedMilliseconds,
            Seed: seedUsed,
            MetadataJson: metadataJson
        );
    }
}
