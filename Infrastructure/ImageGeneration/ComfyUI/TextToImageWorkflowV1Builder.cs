using Application.Exceptions;
using Application.Interfaces;

namespace Infrastructure.ImageGeneration.ComfyUI;

public sealed class TextToImageWorkflowV1Builder : IComfyUIWorkflowBuilder
{
    public string WorkflowName => "TextToImage";
    public int WorkflowVersion => 1;

    public Dictionary<string, object> BuildWorkflow(ImageGenerationRequest request, string resolvedReferenceImageName)
    {
        var defaultNegative = "2girls, 2boys, multiple people, group, crowd, duo, couple, 2persons, extra person, deformed horns, extra horns, asymmetrical malformed horns, bad anatomy, bad hands, missing fingers, extra digits, cropped, signature, watermark, blurry, low quality, worst quality, mutated, text, error";
        var negativePrompt = !string.IsNullOrWhiteSpace(request.NegativePrompt) ? request.NegativePrompt : defaultNegative;

        var modelName = !string.IsNullOrWhiteSpace(request.Model) ? request.Model : "meinamix_meinaV11.safetensors";
        var seed = request.Seed ?? Random.Shared.Next(1, int.MaxValue);
        var width = request.Width > 0 ? (request.Width > 1024 ? 512 : request.Width) : 512;
        var height = request.Height > 0 ? (request.Height > 1024 ? 768 : request.Height) : 768;
        var steps = request.Steps ?? 28;
        var cfg = request.GuidanceScale ?? 7.0f;
        var sampler = !string.IsNullOrWhiteSpace(request.Sampler) ? request.Sampler : "euler_ancestral";
        var scheduler = !string.IsNullOrWhiteSpace(request.Scheduler) ? request.Scheduler : "karras";

        return new Dictionary<string, object>
        {
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
                    ["model"] = new object[] { "4", 0 },
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
                    ["filename_prefix"] = "TextToImage_v1",
                    ["images"] = new object[] { "9", 0 }
                }
            }
        };
    }
}
