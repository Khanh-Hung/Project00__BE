using System.Text.Json;
using System.Text.Json.Nodes;

namespace Domain.ValueObjects;

/// <summary>
/// Provider-agnostic generation profile containing technical parameters and execution metadata.
/// Immutable value object.
/// </summary>
public sealed record GenerationProfile(
    long Seed,
    string Model = "meinamix_meinaV11.safetensors",
    int Width = 512,
    int Height = 768,
    int Steps = 30,
    float Cfg = 7.0f,
    string Sampler = "euler_ancestral",
    string Scheduler = "karras",
    string Workflow = "VisualIdentity",
    int WorkflowVersion = 1,
    string? ParametersJson = null
)
{
    /// <summary>
    /// Immutably creates a copy of this GenerationProfile with overridden conditioning parameters and derived seed.
    /// Preserves Model, Width, Height, Steps, Cfg, Sampler, Scheduler, Workflow, WorkflowVersion,
    /// and all other root/nested JSON properties in ParametersJson.
    /// Invariant: Malformed ParametersJson throws InvalidOperationException (fail-fast, no silent fallback data loss).
    /// </summary>
    public GenerationProfile WithConditioningOverride(
        float slot1Weight,
        float slot1EndAt,
        float slot2Weight,
        float slot2EndAt,
        string weightType = "style transfer",
        long? newSeed = null)
    {
        string updatedParametersJson;

        if (string.IsNullOrWhiteSpace(ParametersJson))
        {
            var node = new JsonObject
            {
                ["ipAdapter"] = new JsonObject
                {
                    ["weight"] = slot1Weight,
                    ["endAt"] = slot1EndAt
                },
                ["sceneContinuity"] = new JsonObject
                {
                    ["weight"] = slot2Weight,
                    ["endAt"] = slot2EndAt,
                    ["weightType"] = weightType
                }
            };
            updatedParametersJson = node.ToJsonString();
        }
        else
        {
            JsonObject node;
            try
            {
                var parsed = JsonNode.Parse(ParametersJson);
                if (parsed is not JsonObject jsonObject)
                {
                    throw new InvalidOperationException("GenerationProfile.ParametersJson must represent a JSON object.");
                }
                node = jsonObject;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "GenerationProfile.ParametersJson is malformed or invalid JSON.",
                    ex);
            }

            // Patch existing ipAdapter node (or create if missing) to preserve any other nested properties
            if (node.TryGetPropertyValue("ipAdapter", out var ipVal) && ipVal is JsonObject ipObj)
            {
                ipObj["weight"] = slot1Weight;
                ipObj["endAt"] = slot1EndAt;
            }
            else
            {
                node["ipAdapter"] = new JsonObject
                {
                    ["weight"] = slot1Weight,
                    ["endAt"] = slot1EndAt
                };
            }

            // Patch existing sceneContinuity node (or create if missing) to preserve any other nested properties
            if (node.TryGetPropertyValue("sceneContinuity", out var contVal) && contVal is JsonObject contObj)
            {
                contObj["weight"] = slot2Weight;
                contObj["endAt"] = slot2EndAt;
                contObj["weightType"] = weightType;
            }
            else
            {
                node["sceneContinuity"] = new JsonObject
                {
                    ["weight"] = slot2Weight,
                    ["endAt"] = slot2EndAt,
                    ["weightType"] = weightType
                };
            }

            updatedParametersJson = node.ToJsonString();
        }

        return this with
        {
            Seed = newSeed ?? Seed,
            ParametersJson = updatedParametersJson
        };
    }

    public static GenerationProfile CreateDefault(
        long? seed = null,
        string? model = null,
        int? width = null,
        int? height = null,
        int? steps = null,
        float? cfg = null,
        string? sampler = null,
        string? scheduler = null,
        string? workflow = null,
        int? workflowVersion = null,
        string? parametersJson = null)
    {
        return new GenerationProfile(
            Seed: seed ?? Random.Shared.NextInt64(1, 999999999),
            Model: model ?? "meinamix_meinaV11.safetensors",
            Width: width ?? 512,
            Height: height ?? 768,
            Steps: steps ?? 30,
            Cfg: cfg ?? 7.0f,
            Sampler: sampler ?? "euler_ancestral",
            Scheduler: scheduler ?? "karras",
            Workflow: workflow ?? "VisualIdentity",
            WorkflowVersion: workflowVersion ?? 1,
            ParametersJson: parametersJson
        );
    }
}
