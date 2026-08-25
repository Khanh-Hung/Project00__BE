using Domain.Enums;

namespace Domain.ValueObjects;

public sealed record CharacterVisualIdentity(
    string? Gender = null,
    string? Face = null,
    string? Hair = null,
    string? Eyes = null,
    string? Skin = null,
    string? Body = null,
    string? AgeAppearance = null,
    string? ClothingStyle = null,
    string? Accessories = null,
    string? VisualTraits = null,
    string? CanonicalReferenceUrl = null,
    string? FullBodyUrl = null,
    GenderPresentation GenderPresentation = GenderPresentation.Female,
    IReadOnlyList<SignatureFeature>? SignatureFeatures = null
)
{
    public GenderPresentation ResolvedGender =>
        GenderPresentation != GenderPresentation.Female
            ? GenderPresentation
            : (Gender?.Equals("Male", StringComparison.OrdinalIgnoreCase) == true
                ? GenderPresentation.Male
                : (Gender?.Equals("Androgynous", StringComparison.OrdinalIgnoreCase) == true
                    ? GenderPresentation.Androgynous
                    : GenderPresentation.Female));
}
