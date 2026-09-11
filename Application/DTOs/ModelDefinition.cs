using Domain.Enums;

namespace Application.DTOs;

/// <summary>
/// Application-facing descriptor for a registered generative model.
/// Expresses model identity, family, and underlying artifact name without leaking workflow or infrastructure logic.
/// </summary>
public sealed record ModelDefinition(
    string Id,
    ModelFamily Family,
    string ArtifactName
);
