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
            else
            {
                workflow = "VisualIdentity";
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
        // 1. Resolve style strictly from VisualIdentity
        var style = character.VisualIdentity?.ResolvedStyle ?? Domain.Enums.VisualStyle.Unspecified;

        // 2. When style is specified:
        if (style != Domain.Enums.VisualStyle.Unspecified)
        {
            // Check configuration override first: AiProviders:ImageGeneration:StyleModels:{Style}
            var configKey = $"AiProviders:ImageGeneration:StyleModels:{style}";
            var configuredStyleModel = _configuration?[configKey]?.Trim();
            if (!string.IsNullOrWhiteSpace(configuredStyleModel))
            {
                return NormalizeModelId(configuredStyleModel);
            }

            // Fallback for other specified styles if global default model is configured
            var configModel = _configuration?["AiProviders:ImageGeneration:DefaultModel"]
                ?? _configuration?["AiProviders:ComfyUI:ModelName"];
            if (!string.IsNullOrWhiteSpace(configModel))
            {
                return NormalizeModelId(configModel.Trim());
            }

            throw new InvalidOperationException(
                $"No image generation model is mapped or configured for VisualStyle '{style}'. " +
                $"Please configure 'AiProviders:ImageGeneration:StyleModels:{style}' in application settings.");
        }

        // 3. When style is strictly Unspecified:
        // If VisualIdentity was provided, visual attributes were supplied but style was omitted/forgotten => FAIL FAST!
        if (character.VisualIdentity != null)
        {
            throw new InvalidOperationException(
                $"Character '{character.Name}' does not have an explicit VisualStyle selected. " +
                "A visual style (e.g. Anime, Realistic) must be explicitly selected before image generation.");
        }

        // 4. For non-visual character instances (e.g. test fixtures without visual identity):
        var defaultModel = _configuration?["AiProviders:ImageGeneration:DefaultModel"]
            ?? _configuration?["AiProviders:ComfyUI:ModelName"];

        if (string.IsNullOrWhiteSpace(defaultModel))
        {
            throw new InvalidOperationException(
                "No default image generation model is configured. " +
                "Please configure 'AiProviders:ImageGeneration:DefaultModel' in application settings.");
        }

        return NormalizeModelId(defaultModel.Trim());
    }

    private string NormalizeModelId(string modelName)
    {
        if (_modelRegistry is null)
        {
            return modelName;
        }

        var definition = _modelRegistry.FindById(modelName);
        if (definition is null)
        {
            throw new InvalidOperationException($"Unknown image generation model '{modelName}'. Model is not registered in ModelRegistry.");
        }

        return definition.Id;
    }
}
