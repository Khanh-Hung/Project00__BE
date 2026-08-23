using Application.Interfaces;
using Infrastructure.ImageGeneration;
using Infrastructure.ImageGeneration.ComfyUI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests;

public sealed class ComfyUIImageGenerationIntegrationTests
{
    private sealed class InMemoryStorageService : IStorageService
    {
        public Task<string> SaveImageAsync(byte[] imageBytes, string fileName, string contentType = "image/jpeg", CancellationToken ct = default)
            => Task.FromResult($"/uploads/{fileName}");

        public Task<string> SaveBase64ImageAsync(string base64Data, string fileName, CancellationToken ct = default)
            => Task.FromResult($"/uploads/{fileName}");

        public Task<bool> DeleteFileAsync(string fileUrl, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    [Fact]
    [Trait("Category", "ComfyUI")]
    public async Task ComfyUI_Generates_Image_Successfully_When_Server_Is_Available()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProviders:ComfyUI:ServerUrl"] = "http://127.0.0.1:8188",
                ["AiProviders:ComfyUI:ModelName"] = "meinamix_meinaV11.safetensors",
                ["AiProviders:ComfyUI:DefaultWidth"] = "512",
                ["AiProviders:ComfyUI:DefaultHeight"] = "768",
                ["AiProviders:ComfyUI:DefaultSteps"] = "30",
                ["AiProviders:ComfyUI:DefaultCfg"] = "7.0",
                ["AiProviders:ComfyUI:DefaultSampler"] = "euler_ancestral",
                ["AiProviders:ComfyUI:DefaultScheduler"] = "karras",
                ["AiProviders:ComfyUI:DefaultIPAdapterWeight"] = "0.45",
                ["AiProviders:ComfyUI:DefaultIPAdapterEndAt"] = "0.70",
                ["AiProviders:ComfyUI:PollIntervalMs"] = "500",
                ["AiProviders:ComfyUI:TimeoutSeconds"] = "120"
            })
            .Build();

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        // Check if local ComfyUI is online before running live generation
        bool isComfyOnline = false;
        try
        {
            var pingRes = await httpClient.GetAsync("http://127.0.0.1:8188/system_stats");
            isComfyOnline = pingRes.IsSuccessStatusCode;
        }
        catch
        {
            isComfyOnline = false;
        }

        if (!isComfyOnline)
        {
            return;
        }

        // Create a temporary reference image on disk to test real upload
        var tempRefPath = Path.Combine(Path.GetTempPath(), "test_canonical_ref.png");
        if (!File.Exists(tempRefPath))
        {
            // Minimal 1x1 PNG byte array
            var minimalPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
            await File.WriteAllBytesAsync(tempRefPath, minimalPng);
        }

        var storageService = new InMemoryStorageService();
        var comfyClient = new ComfyUIClient(httpClient, config, NullLogger<ComfyUIClient>.Instance);
        var inputImageService = new ComfyUIInputImageService(httpClient, config, NullLogger<ComfyUIInputImageService>.Instance);
        var workflowBuilders = new IComfyUIWorkflowBuilder[] { new VisualIdentityWorkflowV1Builder() };
        var service = new ComfyUIImageGenerationService(comfyClient, storageService, inputImageService, workflowBuilders, config, NullLogger<ComfyUIImageGenerationService>.Instance);

        var request = new ImageGenerationRequest(
            Prompt: "masterpiece, best quality, 1girl, solo, (silver long straight hair:1.2), (bright crimson red eyes:1.2), (small black dragon horns on head:1.3), wearing pastel pink sundress, smiling",
            NegativePrompt: "black clothing, red clothing, dark clothing, bad anatomy, bad hands, blurry",
            Width: 512,
            Height: 768,
            Steps: 30,
            GuidanceScale: 7.0f,
            Seed: 9999,
            Model: "meinamix_meinaV11.safetensors",
            Sampler: "euler_ancestral",
            Scheduler: "karras",
            Workflow: "VisualIdentity",
            WorkflowVersion: 1,
            ParametersJson: "{\"ipAdapter\":{\"weight\":0.45,\"endAt\":0.70}}",
            ReferenceImageUrl: tempRefPath
        );

        var result = await service.GenerateImageWithResultAsync(request);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.ImageUrl));
        Assert.Equal("ComfyUI", result.Provider);
        Assert.NotNull(result.ProviderJobId);
        Assert.Equal(9999, result.Seed);
        Assert.True(result.DurationMs > 0);
    }
}
