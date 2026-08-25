using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Domain value object encapsulating compiled prompt invariants for character gender presentation.
/// Centralizes positive and negative tokens across all prompt compilation pipelines.
/// </summary>
public sealed record GenderPromptInvariant(
    GenderPresentation Gender,
    string? PositiveTokens,
    string? NegativeTokens
)
{
    public static GenderPromptInvariant Resolve(GenderPresentation gender)
    {
        return gender switch
        {
            GenderPresentation.Male => new GenderPromptInvariant(
                Gender: GenderPresentation.Male,
                PositiveTokens: "1man, male, masculine face",
                NegativeTokens: "1girl, anime girl, female, woman, breasts, feminine face"
            ),
            GenderPresentation.Female => new GenderPromptInvariant(
                Gender: GenderPresentation.Female,
                PositiveTokens: "1girl, female, feminine face",
                NegativeTokens: "1man, anime man, male, boy, masculine face, facial hair, beard, mustache"
            ),
            GenderPresentation.Androgynous => new GenderPromptInvariant(
                Gender: GenderPresentation.Androgynous,
                PositiveTokens: "androgynous, 1person",
                NegativeTokens: null
            ),
            GenderPresentation.NonBinary => new GenderPromptInvariant(
                Gender: GenderPresentation.NonBinary,
                PositiveTokens: "non-binary, 1person, androgynous appearance",
                NegativeTokens: null
            ),
            _ => new GenderPromptInvariant(
                Gender: GenderPresentation.Unspecified,
                PositiveTokens: null,
                NegativeTokens: null
            )
        };
    }
}
