using System.Globalization;
using System.Text.Json;
using Application.Interfaces;
using Domain.Entities;
using Domain.ValueObjects;
using Microsoft.Extensions.Configuration;

namespace Application.Services;

public sealed class VisualGenerationProfileProvider : IVisualGenerationProfileProvider
{
    private const float DefaultWeight = 0.45f;
    private const float DefaultEndAt = 0.70f;
    private const int DefaultWorkflowVersion = 1;
    public const string DefaultModelFallback = "meinamix_meinaV11.safetensors";

    private readonly IConfiguration? _configuration;
    private readonly IModelRegistry? _modelRegistry;

    public VisualGenerationProfileProvider(IConfiguration? configuration = null, IModelRegistry? modelRegistry = null)
    {
        _configuration = configuration;
        _modelRegistry = modelRegistry;
    }

    /// <summary>
    /// Resolves the generation profile for a turn snapshot.
    /// Note: Character is accepted to support future per-character generation policies; current baseline resolves from configuration.
    /// </summary>
    public GenerationProfile ResolveProfile(
        Character character,
        string? workflowOverride = null,
        bool isTransition = false,
        bool isColdStart = false)
    {
        string? workflow = null;
        if (workflowOverride != null)
        {
            if (string.IsNullOrWhiteSpace(workflowOverride))
            {
                throw new InvalidOperationException("workflowOverride cannot be empty or whitespace.");
            }
            workflow = workflowOverride.Trim();
        }
        else
        {
            var configWorkflow = _configuration?["AiProviders:ImageGeneration:DefaultWorkflow"];
            if (!string.IsNullOrWhiteSpace(configWorkflow))
            {
                workflow = configWorkflow.Trim();
            }
        }

        // 1. Strict validation of WorkflowVersion (missing => default 1; present but invalid => fail-fast)
        int workflowVersion = DefaultWorkflowVersion;
        var workflowVersionStr = _configuration?["AiProviders:ImageGeneration:DefaultWorkflowVersion"];
        if (!string.IsNullOrWhiteSpace(workflowVersionStr))
        {
            if (!int.TryParse(workflowVersionStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedVer) || parsedVer <= 0)
            {
                throw new InvalidOperationException(
                    $"Invalid configuration for 'AiProviders:ImageGeneration:DefaultWorkflowVersion': '{workflowVersionStr}'. WorkflowVersion must be a positive integer greater than 0.");
            }
            workflowVersion = parsedVer;
        }

        // 2. Strict validation of IP-Adapter Weight (missing => default 0.45; present but invalid => fail-fast)
        float weight = DefaultWeight;
        var weightStr = _configuration?["AiProviders:ImageGeneration:IPAdapter:Weight"];
        if (!string.IsNullOrWhiteSpace(weightStr))
        {
            if (!float.TryParse(weightStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedWeight)
                || float.IsNaN(parsedWeight)
                || float.IsInfinity(parsedWeight)
                || parsedWeight < 0.0f
                || parsedWeight > 1.0f)
            {
                throw new InvalidOperationException(
                    $"Invalid configuration for 'AiProviders:ImageGeneration:IPAdapter:Weight': '{weightStr}'. Weight must be a valid number between 0.0 and 1.0.");
            }
            weight = parsedWeight;
        }

        // 3. Strict validation of IP-Adapter EndAt (missing => default 0.70; present but invalid => fail-fast)
        float endAt = DefaultEndAt;
        var endAtStr = _configuration?["AiProviders:ImageGeneration:IPAdapter:EndAt"];
        if (!string.IsNullOrWhiteSpace(endAtStr))
        {
            if (!float.TryParse(endAtStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedEndAt)
                || float.IsNaN(parsedEndAt)
                || float.IsInfinity(parsedEndAt)
                || parsedEndAt < 0.0f
                || parsedEndAt > 1.0f)
            {
                throw new InvalidOperationException(
                    $"Invalid configuration for 'AiProviders:ImageGeneration:IPAdapter:EndAt': '{endAtStr}'. EndAt must be a valid number between 0.0 and 1.0.");
            }
            endAt = parsedEndAt;
        }

        // 4. Resolve Context-Aware Scene Continuity Parameters from Policy
        var effectivePolicy = Slot2ConditioningPolicy.FromConfiguration(_configuration);
        var decision = effectivePolicy.Decide(isColdStart, isTransition);

        float sceneWeight = decision.Weight;
        float sceneEndAt = decision.EndAt;
        string weightType = decision.Mode switch
        {
            Domain.Enums.Slot2ConditioningMode.SceneStyleContinuity => "style transfer",
            Domain.Enums.Slot2ConditioningMode.FullLinearContinuity => "linear",
            _ => "style transfer"
        };

        // 5. Resolve Model for Character based on Aesthetic Style Policy and Configuration
        string model = ResolveModelForCharacter(character);

        // 6. Invariant: ParametersJson is built strictly from validated typed parameters using deterministic JSON serialization
        var parametersJson = JsonSerializer.Serialize(new
        {
            ipAdapter = new
            {
                weight,
                endAt
            },
            sceneContinuity = new
            {
                weight = sceneWeight,
                endAt = sceneEndAt,
                weightType = weightType
            }
        });

        return GenerationProfile.CreateDefault(
            model: model,
            workflow: workflow,
            workflowVersion: workflowVersion,
            parametersJson: parametersJson
        );
    }

    private string ResolveModelForCharacter(Character character)
    {
        var style = character.VisualIdentity?.ResolvedStyle ?? Domain.Enums.VisualStyle.Unspecified;

        // 1. Style-based model resolution if style is specified
        if (style != Domain.Enums.VisualStyle.Unspecified)
        {
            // Check configuration override first: AiProviders:ImageGeneration:StyleModels:{Style}
            var configKey = $"AiProviders:ImageGeneration:StyleModels:{style}";
            var configuredStyleModel = _configuration?[configKey]?.Trim();
            if (!string.IsNullOrWhiteSpace(configuredStyleModel))
            {
                return NormalizeModelId(configuredStyleModel);
            }

            // Built-in style-to-model baseline mappings
            if (style == Domain.Enums.VisualStyle.Realistic || style == Domain.Enums.VisualStyle.Cinematic)
            {
                return NormalizeModelId("epicrealism");
            }

            if (style == Domain.Enums.VisualStyle.Anime || style == Domain.Enums.VisualStyle.Manhwa)
            {
                return NormalizeModelId("meinamix");
            }
        }

        // 2. Global model configuration: AiProviders:ImageGeneration:DefaultModel or AiProviders:ComfyUI:ModelName
        var configModel = _configuration?["AiProviders:ImageGeneration:DefaultModel"]
            ?? _configuration?["AiProviders:ComfyUI:ModelName"];
        if (configModel != null)
        {
            if (string.IsNullOrWhiteSpace(configModel))
            {
                throw new InvalidOperationException("Configured model name cannot be empty or whitespace.");
            }
            return NormalizeModelId(configModel.Trim());
        }

        // 3. Fallback default model
        return DefaultModelFallback;
    }

    private string NormalizeModelId(string modelName)
    {
        if (_modelRegistry != null)
        {
            var def = _modelRegistry.FindById(modelName);
            if (def != null)
            {
                return def.Id;
            }
        }

        return modelName;
    }
}
