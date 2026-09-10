namespace Domain.Enums;

/// <summary>
/// Model-agnostic visual style and aesthetic representation.
/// Represents visual intent independent of model family or generation provider.
/// </summary>
public enum VisualStyle
{
    Unspecified = 0,
    Anime = 1,
    Realistic = 2,
    SemiRealistic = 3,
    Manhwa = 4,
    ThreeDimensional = 5,
    Watercolor = 6,
    PixelArt = 7,
    Cinematic = 8
}
