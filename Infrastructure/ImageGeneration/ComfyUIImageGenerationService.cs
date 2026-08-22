using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Application.Exceptions;
using Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.ImageGeneration;

public class ComfyUIImageGenerationService : IImageGenerationService
{
    private readonly HttpClient _httpClient;
    private readonly IStorageService _storageService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ComfyUIImageGenerationService> _logger;

    public ComfyUIImageGenerationService(
        HttpClient httpClient,
        IStorageService storageService,
        IConfiguration configuration,
        ILogger<ComfyUIImageGenerationService> logger)
    {
        _httpClient = httpClient;
        _storageService = storageService;
        _configuration = configuration;
        _logger = logger;
    }

    public Task<string> GenerateImageAsync(
        string prompt,
        int width = 512,
        int height = 768,
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

        var serverUrl = _configuration["AiProviders:ComfyUI:ServerUrl"] 
            ?? _configuration["AiProviders:DedicatedServerUrl"]
            ?? "http://127.0.0.1:8188";

        var pollIntervalMs = int.TryParse(_configuration["AiProviders:ComfyUI:PollIntervalMs"], out var pollMs) ? pollMs : 500;
        var timeoutSeconds = int.TryParse(_configuration["AiProviders:ComfyUI:TimeoutSeconds"], out var timeoutSec) ? timeoutSec : 60;
        var defaultModel = _configuration["AiProviders:ComfyUI:ModelName"] ?? "meinamix_meinaV11.safetensors";

        float ipAdapterWeight = 0.45f;
        float ipAdapterEndAt = 0.70f;
        if (float.TryParse(_configuration["AiProviders:ComfyUI:DefaultIPAdapterWeight"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedWeight)) ipAdapterWeight = parsedWeight;
        if (float.TryParse(_configuration["AiProviders:ComfyUI:DefaultIPAdapterEndAt"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedEndAt)) ipAdapterEndAt = parsedEndAt;

        var referenceImage = ResolveReferenceImageName(request.ReferenceImageUrl);
        var modelName = !string.IsNullOrWhiteSpace(request.Model) ? request.Model : defaultModel;
        var seed = request.Seed ?? Random.Shared.NextInt64(1, 999999999);
        var width = request.Width > 0 ? request.Width : 512;
        var height = request.Height > 0 ? request.Height : 768;
        var steps = request.Steps ?? 30;
        var cfg = request.GuidanceScale ?? 7.0f;
        var sampler = !string.IsNullOrWhiteSpace(request.Sampler) ? request.Sampler : "euler_ancestral";
        var scheduler = !string.IsNullOrWhiteSpace(request.Scheduler) ? request.Scheduler : "karras";

        var workflow = BuildComfyUIWorkflow(
            positivePrompt: request.Prompt,
            negativePrompt: request.NegativePrompt ?? "black clothing, red clothing, dark clothing, no horns, bad anatomy, bad hands, blurry, low quality",
            referenceImage: referenceImage,
            modelName: modelName,
            width: width,
            height: height,
            steps: steps,
            cfg: cfg,
            sampler: sampler,
            scheduler: scheduler,
            seed: seed,
            ipAdapterWeight: ipAdapterWeight,
            ipAdapterEndAt: ipAdapterEndAt
        );

        var promptEndpoint = serverUrl.TrimEnd('/') + "/prompt";
        string promptId;

        try
        {
            var promptPayload = new { prompt = workflow };
            var res = await _httpClient.PostAsJsonAsync(promptEndpoint, promptPayload, ct);
            if (!res.IsSuccessStatusCode)
            {
                var errorBody = await res.Content.ReadAsStringAsync(ct);
                var statusCode = (int)res.StatusCode;
                _logger.LogWarning("ComfyUI /prompt returned HTTP {Status}: {ErrorBody}", statusCode, errorBody);

                if (statusCode == 408 || statusCode == 429 || statusCode >= 500)
                {
                    throw new GpuTransientException($"ComfyUI transient error (HTTP {statusCode}): {errorBody}", statusCode);
                }
                throw new GpuNonTransientException($"ComfyUI non-transient error (HTTP {statusCode}): {errorBody}", statusCode);
            }

            var resObj = await res.Content.ReadFromJsonAsync<ComfyUIPromptResponse>(cancellationToken: ct);
            promptId = resObj?.PromptId ?? throw new GpuTransientException("ComfyUI did not return prompt_id.");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new GpuTransientException("ComfyUI prompt submission timed out.", (int)HttpStatusCode.RequestTimeout, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new GpuTransientException($"Network error connecting to ComfyUI: {ex.Message}", null, ex);
        }

        _logger.LogInformation("[ComfyUIJobStarted] PromptId={PromptId}, Model={Model}, Seed={Seed}", promptId, modelName, seed);

        // Polling loop
        var historyEndpoint = $"{serverUrl.TrimEnd('/')}/history/{promptId}";
        var timeoutAt = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        string? outputFileName = null;

        while (DateTime.UtcNow < timeoutAt)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

            await Task.Delay(pollIntervalMs, ct);

            try
            {
                var historyRes = await _httpClient.GetAsync(historyEndpoint, ct);
                if (!historyRes.IsSuccessStatusCode) continue;

                var historyJson = await historyRes.Content.ReadAsStringAsync(ct);
                var root = JsonNode.Parse(historyJson)?.AsObject();

                if (root != null && root.ContainsKey(promptId))
                {
                    var promptObj = root[promptId]?.AsObject();
                    var outputs = promptObj?["outputs"]?.AsObject();
                    if (outputs != null && outputs.Count > 0)
                    {
                        // Look for SaveImage node outputs
                        foreach (var nodeEntry in outputs)
                        {
                            var imagesArr = nodeEntry.Value?["images"]?.AsArray();
                            if (imagesArr != null && imagesArr.Count > 0)
                            {
                                outputFileName = imagesArr[0]?["filename"]?.GetValue<string>();
                                break;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(outputFileName))
                        {
                            break;
                        }
                    }

                    // Check for execution error
                    var status = promptObj?["status"]?.AsObject();
                    var statusStr = status?["status_str"]?.GetValue<string>();
                    if (statusStr == "error")
                    {
                        var messages = status?["messages"]?.ToString() ?? "Unknown ComfyUI execution error";
                        throw new GpuNonTransientException($"ComfyUI workflow execution error: {messages}");
                    }
                }
            }
            catch (Exception ex) when (ex is not GpuNonTransientException && ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Transient poll exception for PromptId={PromptId}", promptId);
            }
        }

        if (string.IsNullOrWhiteSpace(outputFileName))
        {
            throw new GpuTransientException($"ComfyUI generation timed out after {timeoutSeconds}s for PromptId={promptId}.");
        }

        // Fetch image bytes
        byte[] imageBytes;
        var viewUrl = $"{serverUrl.TrimEnd('/')}/view?filename={Uri.EscapeDataString(outputFileName)}";

        try
        {
            imageBytes = await _httpClient.GetByteArrayAsync(viewUrl, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download image via /view from ComfyUI for {FileName}", outputFileName);
            throw new GpuTransientException($"Failed to retrieve output image {outputFileName} from ComfyUI: {ex.Message}", null, ex);
        }

        var fileName = $"{promptId}_{outputFileName}";
        var storageUrl = await _storageService.SaveImageAsync(imageBytes, fileName, "image/png", ct);

        stopwatch.Stop();
        _logger.LogInformation("[ComfyUIJobCompleted] PromptId={PromptId}, OutputUrl={Url}, Duration={Ms}ms",
            promptId, storageUrl, stopwatch.ElapsedMilliseconds);

        return new ImageGenerationResult(
            ImageUrl: storageUrl,
            Provider: "ComfyUI",
            ProviderJobId: promptId,
            DurationMs: stopwatch.ElapsedMilliseconds,
            Seed: seed,
            MetadataJson: JsonSerializer.Serialize(new
            {
                model = modelName,
                seed = seed,
                width = width,
                height = height,
                steps = steps,
                cfg = cfg,
                sampler = sampler,
                scheduler = scheduler,
                workflow = request.Workflow,
                workflowVersion = request.WorkflowVersion
            })
        );
    }

    private static string ResolveReferenceImageName(string? referenceUrl)
    {
        if (string.IsNullOrWhiteSpace(referenceUrl)) return "Lyra_tight_face.png";
        
        var clean = Path.GetFileName(referenceUrl.Split('?')[0]);
        if (!string.IsNullOrWhiteSpace(clean)) return clean;

        return "Lyra_tight_face.png";
    }

    private static Dictionary<string, object> BuildComfyUIWorkflow(
        string positivePrompt,
        string negativePrompt,
        string referenceImage,
        string modelName,
        int width,
        int height,
        int steps,
        float cfg,
        string sampler,
        string scheduler,
        long seed,
        float ipAdapterWeight,
        float ipAdapterEndAt)
    {
        return new Dictionary<string, object>
        {
            ["1"] = new
            {
                class_type = "LoadImage",
                inputs = new { image = referenceImage }
            },
            ["2"] = new
            {
                class_type = "IPAdapterUnifiedLoader",
                inputs = new
                {
                    model = new object[] { "4", 0 },
                    preset = "PLUS (high strength)"
                }
            },
            ["10"] = new
            {
                class_type = "IPAdapterAdvanced",
                inputs = new
                {
                    model = new object[] { "2", 0 },
                    ipadapter = new object[] { "2", 1 },
                    image = new object[] { "1", 0 },
                    weight = ipAdapterWeight,
                    weight_type = "ease in-out",
                    combine_embeds = "concat",
                    start_at = 0.0,
                    end_at = ipAdapterEndAt,
                    embeds_scaling = "V only"
                }
            },
            ["3"] = new
            {
                class_type = "KSampler",
                inputs = new
                {
                    cfg = cfg,
                    denoise = 1.0,
                    latent_image = new object[] { "5", 0 },
                    model = new object[] { "10", 0 },
                    negative = new object[] { "7", 0 },
                    positive = new object[] { "6", 0 },
                    sampler_name = sampler,
                    scheduler = scheduler,
                    seed = seed,
                    steps = steps
                }
            },
            ["4"] = new
            {
                class_type = "CheckpointLoaderSimple",
                inputs = new { ckpt_name = modelName }
            },
            ["5"] = new
            {
                class_type = "EmptyLatentImage",
                inputs = new { batch_size = 1, height = height, width = width }
            },
            ["6"] = new
            {
                class_type = "CLIPTextEncode",
                inputs = new
                {
                    clip = new object[] { "4", 1 },
                    text = positivePrompt
                }
            },
            ["7"] = new
            {
                class_type = "CLIPTextEncode",
                inputs = new
                {
                    clip = new object[] { "4", 1 },
                    text = negativePrompt
                }
            },
            ["8"] = new
            {
                class_type = "VAEDecode",
                inputs = new
                {
                    samples = new object[] { "3", 0 },
                    vae = new object[] { "4", 2 }
                }
            },
            ["9"] = new
            {
                class_type = "SaveImage",
                inputs = new
                {
                    filename_prefix = "Project00_Output",
                    images = new object[] { "8", 0 }
                }
            }
        };
    }

    private sealed class ComfyUIPromptResponse
    {
        [JsonPropertyName("prompt_id")]
        public string? PromptId { get; set; }
    }
}
