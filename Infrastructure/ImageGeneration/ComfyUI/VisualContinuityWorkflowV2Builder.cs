using System.Text.Json;
using Application.Exceptions;
using Application.Interfaces;

namespace Infrastructure.ImageGeneration.ComfyUI;

/// <summary>
/// ComfyUI Visual Continuity Workflow V2 Builder.
/// Implements dual-reference conditioning:
/// - Slot 1 (Node 10): Canonical Identity Reference (tight face) with deep identity retention (weight: ~0.60, end_at: ~0.85).
/// - Slot 2 (Node 14): Previous Scene Reference with lighting/palette/outfit continuity (weight: ~0.20, end_at: ~0.40).
/// - Gracefully falls back to single-slot identity mode when PreviousSceneImageUrl is absent (Turn 1 / Cold Start).
/// </summary>
public sealed class VisualContinuityWorkflowV2Builder : IComfyUIWorkflowBuilder
{
    public string WorkflowName => "VisualContinuity";
    public int WorkflowVersion => 2;

    public Dictionary<string, object> BuildWorkflow(ImageGenerationRequest request, string resolvedReferenceImageName)
    {
        return BuildWorkflow(request, resolvedReferenceImageName, null);
    }

    public Dictionary<string, object> BuildWorkflow(
        ImageGenerationRequest request,
        string resolvedReferenceImageName,
        string? resolvedPreviousSceneImageName)
    {
        if (string.IsNullOrWhiteSpace(resolvedReferenceImageName))
        {
            throw new GpuNonTransientException("CanonicalReferenceUrl (tight face crop) is required for VisualContinuity workflow.");
        }

        var defaultNegative = "2girls, 2boys, multiple people, group, crowd, duo, couple, 2persons, extra person, deformed horns, extra horns, asymmetrical malformed horns, bad anatomy, bad hands, missing fingers, extra digits, cropped, signature, watermark, blurry, low quality, worst quality";
        var negativePrompt = !string.IsNullOrWhiteSpace(request.NegativePrompt) ? request.NegativePrompt : defaultNegative;

        var modelName = !string.IsNullOrWhiteSpace(request.Model) ? request.Model : "meinamix_meinaV11.safetensors";
        var seed = request.Seed ?? throw new GpuNonTransientException("Seed is required for deterministic VisualContinuity workflow execution.");
        var width = request.Width > 0 ? request.Width : 512;
        var height = request.Height > 0 ? request.Height : 768;
        var steps = request.Steps ?? 30;
        var cfg = request.GuidanceScale ?? 7.0f;
        var sampler = !string.IsNullOrWhiteSpace(request.Sampler) ? request.Sampler : "euler_ancestral";
        var scheduler = !string.IsNullOrWhiteSpace(request.Scheduler) ? request.Scheduler : "karras";

        // Parse IP-Adapter weights from ParametersJson
        float ipAdapterWeight = request.IdentityScale ?? 0.60f;
        float ipAdapterEndAt = 0.85f;
        float sceneContinuityWeight = request.SceneScale ?? 0.20f;
        float sceneContinuityEndAt = 0.40f;

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
                if (doc.RootElement.TryGetProperty("sceneContinuity", out var scProp))
                {
                    if (scProp.TryGetProperty("weight", out var swProp)) sceneContinuityWeight = (float)swProp.GetDouble();
                    if (scProp.TryGetProperty("endAt", out var seProp)) sceneContinuityEndAt = (float)seProp.GetDouble();
                }
            }
            catch (JsonException ex)
            {
                throw new GpuNonTransientException($"Malformed ParametersJson in VisualSnapshot: {ex.Message}", innerException: ex);
            }
        }

        bool hasPreviousScene = !string.IsNullOrWhiteSpace(resolvedPreviousSceneImageName);

        var graph = new Dictionary<string, object>
        {
            // Node 1: Canonical Reference Image (Avatar Face)
            ["1"] = new Dictionary<string, object>
            {
                ["class_type"] = "LoadImage",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["image"] = resolvedReferenceImageName
                }
            },
            // Node 2: Shared CLIP Vision Loader
            ["2"] = new Dictionary<string, object>
            {
                ["class_type"] = "CLIPVisionLoader",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["clip_name"] = "CLIP-ViT-H-14-laion2B-s32B-b79K.safetensors"
                }
            },
            // Node 8: Shared IP-Adapter Plus Model Loader
            ["8"] = new Dictionary<string, object>
            {
                ["class_type"] = "IPAdapterModelLoader",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["ipadapter_file"] = "ip-adapter-plus_sd15.safetensors"
                }
            },
            // Node 4: Base SD Checkpoint Loader
            ["4"] = new Dictionary<string, object>
            {
                ["class_type"] = "CheckpointLoaderSimple",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["ckpt_name"] = modelName
                }
            },
            // Node 10: Slot 1 - Canonical Identity Conditioning
            ["10"] = new Dictionary<string, object>
            {
                ["class_type"] = "IPAdapterAdvanced",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["weight"] = Math.Round((double)ipAdapterWeight, 4),
                    ["weight_type"] = "linear",
                    ["combine_embeds"] = "concat",
                    ["start_at"] = 0.0,
                    ["end_at"] = Math.Round((double)ipAdapterEndAt, 4),
                    ["embeds_scaling"] = "K+V",
                    ["model"] = new object[] { "4", 0 },
                    ["ipadapter"] = new object[] { "8", 0 },
                    ["image"] = new object[] { "1", 0 },
                    ["clip_vision"] = new object[] { "2", 0 }
                }
            },
            // Node 5: Empty Latent Image
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
            // Node 6: Positive Text Prompt
            ["6"] = new Dictionary<string, object>
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["text"] = request.Prompt,
                    ["clip"] = new object[] { "4", 1 }
                }
            },
            // Node 7: Negative Text Prompt
            ["7"] = new Dictionary<string, object>
            {
                ["class_type"] = "CLIPTextEncode",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["text"] = negativePrompt,
                    ["clip"] = new object[] { "4", 1 }
                }
            }
        };

        // Determine KSampler model input source (Dual IP-Adapter chained or Single-Slot fallback)
        object[] ksamplerModelSource;

        if (hasPreviousScene)
        {
            // Node 13: Previous Turn Scene Image
            graph["13"] = new Dictionary<string, object>
            {
                ["class_type"] = "LoadImage",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["image"] = resolvedPreviousSceneImageName!
                }
            };

            // Node 14: Slot 2 - Scene Continuity Conditioning chained onto Node 10
            graph["14"] = new Dictionary<string, object>
            {
                ["class_type"] = "IPAdapterAdvanced",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["weight"] = Math.Round((double)sceneContinuityWeight, 4),
                    ["weight_type"] = "linear",
                    ["combine_embeds"] = "concat",
                    ["start_at"] = 0.0,
                    ["end_at"] = Math.Round((double)sceneContinuityEndAt, 4),
                    ["embeds_scaling"] = "K+V",
                    ["model"] = new object[] { "10", 0 }, // Chained from Slot 1 output!
                    ["ipadapter"] = new object[] { "8", 0 }, // Shared IP-Adapter model
                    ["image"] = new object[] { "13", 0 }, // Previous Scene Image
                    ["clip_vision"] = new object[] { "2", 0 } // Shared CLIP Vision model
                }
            };

            ksamplerModelSource = new object[] { "14", 0 };
        }
        else
        {
            // Fallback for Turn 1: Connect Slot 1 model directly to KSampler
            ksamplerModelSource = new object[] { "10", 0 };
        }

        // Node 3: KSampler
        graph["3"] = new Dictionary<string, object>
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
                ["model"] = ksamplerModelSource,
                ["positive"] = new object[] { "6", 0 },
                ["negative"] = new object[] { "7", 0 },
                ["latent_image"] = new object[] { "5", 0 }
            }
        };

        // Node 9: VAE Decode
        graph["9"] = new Dictionary<string, object>
        {
            ["class_type"] = "VAEDecode",
            ["inputs"] = new Dictionary<string, object>
            {
                ["samples"] = new object[] { "3", 0 },
                ["vae"] = new object[] { "4", 2 }
            }
        };

        // Node 11: Save Image
        graph["11"] = new Dictionary<string, object>
        {
            ["class_type"] = "SaveImage",
            ["inputs"] = new Dictionary<string, object>
            {
                ["filename_prefix"] = "VisualContinuity_v2",
                ["images"] = new object[] { "9", 0 }
            }
        };

        return graph;
    }
}
