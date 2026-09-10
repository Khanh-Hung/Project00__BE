using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Encapsulates model-independent compiled prompt invariants for visual style presentation.
/// Maps high-level visual styles into positive intent tokens and opposing negative tokens.
/// </summary>
public sealed record VisualStyleDefinition(
    VisualStyle Style,
    string? PositiveTokens,
    string? NegativeTokens
)
{
    public static VisualStyleDefinition Resolve(VisualStyle style, string? customStyle = null)
    {
        if (style == VisualStyle.Unspecified && !string.IsNullOrWhiteSpace(customStyle))
        {
            if (Enum.TryParse<VisualStyle>(customStyle, ignoreCase: true, out var parsed))
            {
                style = parsed;
            }
            else
            {
                return new VisualStyleDefinition(
                    Style: VisualStyle.Unspecified,
                    PositiveTokens: customStyle.Trim(),
                    NegativeTokens: null
                );
            }
        }

        return style switch
        {
            VisualStyle.Anime => new VisualStyleDefinition(
                Style: VisualStyle.Anime,
                PositiveTokens: "anime style, vibrant anime aesthetic, clean lines",
                NegativeTokens: "photorealistic, realistic, 3d render"
            ),
            VisualStyle.Realistic => new VisualStyleDefinition(
                Style: VisualStyle.Realistic,
                PositiveTokens: "photorealistic, realistic, natural skin texture, lifelike details, authentic lighting, photography",
                NegativeTokens: "anime, cartoon, comic, illustration, drawing, 3d render, stylized, doll"
            ),
            VisualStyle.SemiRealistic => new VisualStyleDefinition(
                Style: VisualStyle.SemiRealistic,
                PositiveTokens: "semi-realistic, digital painting, delicate rendered details, subtle realism",
                NegativeTokens: "flat 2d, heavy anime, low quality"
            ),
            VisualStyle.Manhwa => new VisualStyleDefinition(
                Style: VisualStyle.Manhwa,
                PositiveTokens: "manhwa art style, webtoon aesthetic, elegant sharp lineart, expressive digital color",
                NegativeTokens: "photorealistic, 3d render, western comic"
            ),
            VisualStyle.ThreeDimensional => new VisualStyleDefinition(
                Style: VisualStyle.ThreeDimensional,
                PositiveTokens: "3d digital render, octane render, smooth subsurface scattering, detailed 3d model",
                NegativeTokens: "flat 2d, sketch, traditional drawing"
            ),
            VisualStyle.Watercolor => new VisualStyleDefinition(
                Style: VisualStyle.Watercolor,
                PositiveTokens: "watercolor painting, soft fluid pigment washes, paper texture, painterly brush strokes",
                NegativeTokens: "photorealistic, 3d render, sharp vector"
            ),
            VisualStyle.PixelArt => new VisualStyleDefinition(
                Style: VisualStyle.PixelArt,
                PositiveTokens: "pixel art, 16-bit aesthetic, crisp pixel edges, retro game style",
                NegativeTokens: "blurry, smooth shading, high resolution photograph, 3d render"
            ),
            VisualStyle.Cinematic => new VisualStyleDefinition(
                Style: VisualStyle.Cinematic,
                PositiveTokens: "cinematic film still, 35mm photograph, dramatic color grading, atmospheric lighting, depth of field",
                NegativeTokens: "anime, cartoon, drawing, flat lighting"
            ),
            _ => new VisualStyleDefinition(
                Style: VisualStyle.Unspecified,
                PositiveTokens: null,
                NegativeTokens: null
            )
        };
    }
}
