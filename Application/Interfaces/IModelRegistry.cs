using Application.DTOs;

namespace Application.Interfaces;

/// <summary>
/// Read-side registry contract for generative model definitions.
/// Resolves semantic model identifiers to their model definitions.
/// </summary>
public interface IModelRegistry
{
    /// <summary>
    /// Finds a model definition by its semantic model ID (case-insensitive).
    /// Returns null if the model is not registered.
    /// </summary>
    ModelDefinition? FindById(string modelId);

    /// <summary>
    /// Gets a required model definition by its semantic model ID.
    /// Throws KeyNotFoundException if the model is not registered.
    /// </summary>
    ModelDefinition GetRequired(string modelId);

    /// <summary>
    /// Returns all registered model definitions.
    /// </summary>
    IReadOnlyCollection<ModelDefinition> GetAll();
}
