using Application.DTOs;
using Application.Interfaces;
using Domain.Enums;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Services;

/// <summary>
/// Configuration-backed implementation of IModelRegistry.
/// Resolves model definitions from application configuration (AiProviders:ImageGeneration:Models)
/// and provides built-in baseline default models with transparent legacy artifact fallback.
/// </summary>
public sealed class ConfigurationModelRegistry : IModelRegistry
{
    private static readonly ModelDefinition[] BaselineModels =
    [
        new ModelDefinition("meinamix", ModelFamily.Sd15, "meinamix_meinaV11.safetensors"),
        new ModelDefinition("epicrealism", ModelFamily.Sd15, "epicrealism_naturalSin.safetensors"),
        new ModelDefinition("anime3xl", ModelFamily.Sdxl, "animagineXLV3_base.safetensors"),
        new ModelDefinition("flux-dev", ModelFamily.Flux, "flux1-dev.safetensors")
    ];

    private readonly Dictionary<string, ModelDefinition> _modelsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelDefinition> _modelsByArtifactName = new(StringComparer.OrdinalIgnoreCase);

    public ConfigurationModelRegistry(IConfiguration? configuration = null)
    {
        // 1. Populate baseline standard models
        foreach (var baseline in BaselineModels)
        {
            RegisterModel(baseline);
        }

        // 2. Overlay configured models if configuration section exists
        var modelsSection = configuration?.GetSection("AiProviders:ImageGeneration:Models");
        if (modelsSection != null && modelsSection.Exists())
        {
            foreach (var child in modelsSection.GetChildren())
            {
                var id = child["Id"]?.Trim();
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = child.Key?.Trim();
                }

                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var artifactName = child["ArtifactName"]?.Trim() ?? child["CheckpointName"]?.Trim();
                if (string.IsNullOrWhiteSpace(artifactName))
                    continue;

                var family = ModelFamily.Unspecified;
                var familyStr = child["Family"]?.Trim();
                if (!string.IsNullOrWhiteSpace(familyStr) && Enum.TryParse<ModelFamily>(familyStr, ignoreCase: true, out var parsedFamily))
                {
                    family = parsedFamily;
                }

                RegisterModel(new ModelDefinition(id, family, artifactName));
            }
        }
    }

    private void RegisterModel(ModelDefinition model)
    {
        _modelsById[model.Id] = model;
        if (!string.IsNullOrWhiteSpace(model.ArtifactName))
        {
            _modelsByArtifactName[model.ArtifactName] = model;
        }
    }

    public ModelDefinition? FindById(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var key = modelId.Trim();

        // 1. Canonical lookup by ModelId
        if (_modelsById.TryGetValue(key, out var model))
            return model;

        // 2. Legacy fallback lookup by underlying artifact filename
        if (_modelsByArtifactName.TryGetValue(key, out var legacyModel))
            return legacyModel;

        return null;
    }

    public ModelDefinition GetRequired(string modelId)
    {
        return FindById(modelId)
            ?? throw new KeyNotFoundException($"Model with ID '{modelId}' is not registered in the ModelRegistry.");
    }

    public IReadOnlyCollection<ModelDefinition> GetAll() => _modelsById.Values;
}
