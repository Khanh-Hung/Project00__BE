namespace Domain.Enums;

/// <summary>
/// Represents the foundational architecture or family of a generative model.
/// Decouples model checkpoints and artifacts from engine-specific workflow implementations.
/// </summary>
public enum ModelFamily
{
    Unspecified = 0,
    Sd15 = 1,
    Sdxl = 2,
    Flux = 3
}
