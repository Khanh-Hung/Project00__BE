using System.Text.Json;
using Application.Exceptions;
using Application.Interfaces;

namespace Infrastructure.ImageGeneration.ComfyUI;

public sealed class VisualIdentityWorkflowV1Builder : IComfyUIWorkflowBuilder
{
    public string WorkflowName => "VisualIdentity";
    public int WorkflowVersion => 1;

    public Dictionary<string, object> BuildWorkflow(ImageGenerationRequest request, string resolvedReferenceImageName)
    {
        if (string.IsNullOrWhiteSpace(resolvedReferenceImageName))
        {
            throw new GpuNonTransientException("CanonicalReferenceUrl (tight face crop) is required for VisualIdentity workflow.");
        }

        var defaultNegative = "deformed horns, extra horns, asymmetrical malformed horns, bad anatomy, bad hands, missing fingers, extra digits, cropped, signature, watermark, blurry, low quality, worst quality";
        var negativePrompt = !string.IsNullOrWhiteSpace(request.NegativePrompt) ? request.NegativePrompt : defaultNegative;

        var modelName = !string.IsNullOrWhiteSpace(request.Model) ? request.Model : "meinamix_meinaV11.safetensors";
        var seed = request.Seed ?? Random.Shared.NextInt64(1, 999999999);
        var width = request.Width > 0 ? request.Width : 512;
        var height = request.Height > 0 ? request.Height : 768;
        var steps = request.Steps ?? 30;
        var cfg = request.GuidanceScale ?? 7.0f;
        var sampler = !string.IsNullOrWhiteSpace(request.Sampler) ? request.Sampler : "euler_ancestral";
        var scheduler = !string.IsNullOrWhiteSpace(request.Scheduler) ? request.Scheduler : "karras";

        // Parse IPAdapter weights from ParametersJson (fallback 0.45 / 0.70)
        float ipAdapterWeight = 0.45f;
        float ipAdapterEndAt = 0.70f;

        if (!string.IsNullOrWhiteSpace(request.ParametersJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(request.ParametersJson);
                if (doc.RootElement.TryGetProperty("ipAdapter", out var ipProp))
                {
                    if (ipProp.TryGetProperty("weight", out var wProp)) ipAdapterWeight = (float)wProp.GetDouble();
                    if (ipProp.TryGetProperty("endAt", out var eProp)) ipAdapterEndAt = (float)eProp.GetDouble();
                }
            }
            catch (JsonException ex)
            {
                throw new GpuNonTransientException($"Malformed ParametersJson in VisualSnapshot: {ex.Message}", innerException: ex);
            }
        }

        return new Dictionary<string, object>
        {
            ["1"] = new Dictionary<string, object>
            {
                ["class_type"] = "LoadImage",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["image"] = resolvedReferenceImageName
                }
            },
            ["2"] = new Dictionary<string, object>
            {
                ["class_type"] = "CLIPVisionLoader",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["clip_name"] = "CLIP-ViT-H-14-laion2B-s32B-b79K.safetensors"
                }
            },
            ["8"] = new Dictionary<string, object>
            {
                ["class_type"] = "IPAdapterModelLoader",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["ipadapter_file"] = "ip-adapter-plus_sd15.safetensors"
                }
            },
            ["10"] = new Dictionary<string, object>
            {
                ["class_type"] = "IPAdapterAdvanced",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["weight"] = (double)ipAdapterWeight,
                    ["weight_type"] = "linear",
                    ["combine_embeds"] = "concat",
                    ["start_at"] = 0.0,
                    ["end_at"] = (double)ipAdapterEndAt,
                    ["embeds_scaling"] = "V only",
                    ["model"] = new object[] { "4", 0 },
                    ["ipadapter"] = new object[] { "8", 0 },
                    ["image"] = new object[] { "1", 0 },
                    ["clip_vision"] = new object[] { "2", 0 }
                }
            },
            ["4"] = new Dictionary<string, object>
            {
                ["class_type"] = "CheckpointLoaderSimple",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["ckpt_name"] = modelName
                }
            },
            ["5"] = new Dictionary<string, object>
            {
                ["class_type"] = "EmptyLatentImage",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["width"] = width,
                    ["height"] = height,
                    ["batch_size"] = 1
                }
            },
            ["6"] = new Dictionary<string, object>
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["text"] = request.Prompt,
                    ["clip"] = new object[] { "4", 1 }
                }
            },
            ["7"] = new Dictionary<string, object>
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["text"] = negativePrompt,
                    ["clip"] = new object[] { "4", 1 }
                }
            },
            ["3"] = new Dictionary<string, object>
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["seed"] = seed,
                    ["steps"] = steps,
                    ["cfg"] = (double)cfg,
                    ["sampler_name"] = sampler,
                    ["scheduler"] = scheduler,
                    ["denoise"] = 1.0,
                    ["model"] = new object[] { "10", 0 },
                    ["positive"] = new object[] { "6", 0 },
                    ["negative"] = new object[] { "7", 0 },
                    ["latent_image"] = new object[] { "5", 0 }
                }
            },
            ["9"] = new Dictionary<string, object>
            {
                ["class_type"] = "VAEDecode",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["samples"] = new object[] { "3", 0 },
                    ["vae"] = new object[] { "4", 2 }
                }
            },
            ["11"] = new Dictionary<string, object>
            {
                ["class_type"] = "SaveImage",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["filename_prefix"] = "VisualIdentity_v1",
                    ["images"] = new object[] { "9", 0 }
                }
            }
        };
    }
}
