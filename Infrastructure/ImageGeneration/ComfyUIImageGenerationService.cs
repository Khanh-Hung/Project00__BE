using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Application.Exceptions;
using Application.Interfaces;
using Infrastructure.ImageGeneration.ComfyUI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ImageGeneration;

public sealed class ComfyUIImageGenerationService : IImageGenerationService
{
    private readonly IComfyUIClient _comfyClient;
    private readonly IStorageService _storageService;
    private readonly IComfyUIInputImageService _inputImageService;
    private readonly IEnumerable<IComfyUIWorkflowBuilder> _workflowBuilders;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ComfyUIImageGenerationService> _logger;

    public ComfyUIImageGenerationService(
        IComfyUIClient comfyClient,
        IStorageService storageService,
        IComfyUIInputImageService inputImageService,
        IEnumerable<IComfyUIWorkflowBuilder> workflowBuilders,
        IConfiguration configuration,
        ILogger<ComfyUIImageGenerationService> logger)
    {
        _comfyClient = comfyClient;
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

        int pollIntervalMs = 500;
        int timeoutSeconds = 120;
        if (int.TryParse(_configuration["AiProviders:ComfyUI:PollIntervalMs"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInterval)) pollIntervalMs = parsedInterval;
        if (int.TryParse(_configuration["AiProviders:ComfyUI:TimeoutSeconds"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTimeout)) timeoutSeconds = parsedTimeout;

        var targetWorkflow = request.Workflow ?? (string.IsNullOrWhiteSpace(request.ReferenceImageUrl) ? "TextToImage" : "VisualIdentity");
        var targetVersion = request.WorkflowVersion;
        string promptId = request.ProviderJobId ?? string.Empty;
        IComfyUIWorkflowBuilder? builder = null;

        if (string.IsNullOrWhiteSpace(promptId))
        {
            // 1. Ensure Reference Image is uploaded if required by workflow
            string resolvedReferenceImageName = string.Empty;
            if (!string.IsNullOrWhiteSpace(request.ReferenceImageUrl))
            {
                resolvedReferenceImageName = await _inputImageService.EnsureImageUploadedAsync(request.ReferenceImageUrl, ct);
            }

            // 2. Select Workflow Builder by exact (Workflow, WorkflowVersion) match - NO SILENT FALLBACK!
            builder = _workflowBuilders.FirstOrDefault(b =>
                string.Equals(b.WorkflowName, targetWorkflow, StringComparison.OrdinalIgnoreCase) &&
                b.WorkflowVersion == targetVersion)
                ?? throw new GpuNonTransientException($"ComfyUI workflow '{targetWorkflow}' with version {targetVersion} is not available on this server.");

            var workflowGraph = builder.BuildWorkflow(request, resolvedReferenceImageName);

            // 3. Post Prompt Graph to ComfyUI Client
            promptId = await _comfyClient.QueuePromptAsync(workflowGraph, ct);
            _logger.LogInformation("ComfyUI prompt enqueued: PromptId={PromptId}, Workflow={Workflow}, Version={Version}", promptId, targetWorkflow, targetVersion);

            // Immediately persist ProviderJobId before beginning polling
            if (request.OnPromptQueuedAsync != null)
            {
                await request.OnPromptQueuedAsync(promptId, ct);
            }
        }
        else
        {
            _logger.LogInformation("ComfyUI recovery: Resuming polling for existing ProviderJobId={PromptId}, Workflow={Workflow}, Version={Version}",
                promptId, targetWorkflow, targetVersion);
        }

        // 4. Poll history until completed or timeout
        var startTime = DateTime.UtcNow;
        var timeoutLimit = TimeSpan.FromSeconds(timeoutSeconds);
        string? outputFilename = null;
        string? outputSubfolder = null;
        string? outputType = null;

        while (DateTime.UtcNow - startTime < timeoutLimit)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(pollIntervalMs, ct);

            var historyResult = await _comfyClient.GetHistoryAsync(promptId, ct);
            if (historyResult != null)
            {
                if (!historyResult.IsSuccess)
                {
                    _logger.LogError("ComfyUI execution failed for PromptId={PromptId}: {Error}", promptId, historyResult.ErrorMessage);
                    throw new GpuNonTransientException($"ComfyUI workflow execution failed: {historyResult.ErrorMessage}");
                }

                if (historyResult.OutputImages.Count > 0)
                {
                    var firstImg = historyResult.OutputImages[0];
                    outputFilename = firstImg.Filename;
                    outputSubfolder = firstImg.Subfolder;
                    outputType = firstImg.Type;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(outputFilename))
        {
            throw new GpuTransientException($"ComfyUI generation timed out after {timeoutSeconds}s for PromptId={promptId}.");
        }

        // 5. Download rendered image via ComfyUI Client
        var imageBytes = await _comfyClient.DownloadImageAsync(outputFilename, outputSubfolder, outputType, ct);

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
            Workflow = builder?.WorkflowName ?? targetWorkflow,
            WorkflowVersion = builder?.WorkflowVersion ?? targetVersion,
            Width = request.Width,
            Height = request.Height,
            Steps = request.Steps,
            Cfg = request.GuidanceScale,
            Sampler = request.Sampler,
            Scheduler = request.Scheduler,
            ParametersJson = request.ParametersJson
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
