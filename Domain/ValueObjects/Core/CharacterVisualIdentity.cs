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
    GenderPresentation Presentation = GenderPresentation.Unspecified,
    IReadOnlyList<SignatureFeature>? SignatureFeatures = null
)
{
    public GenderPresentation ResolvedGender
    {
        get
        {
            if (Presentation != GenderPresentation.Unspecified)
                return Presentation;

            if (string.IsNullOrWhiteSpace(Gender))
                return GenderPresentation.Unspecified;

            if (Gender.Equals("Male", StringComparison.OrdinalIgnoreCase) ||
                Gender.Equals("Man", StringComparison.OrdinalIgnoreCase) ||
                Gender.Equals("Boy", StringComparison.OrdinalIgnoreCase))
                return GenderPresentation.Male;

            if (Gender.Equals("Female", StringComparison.OrdinalIgnoreCase) ||
                Gender.Equals("Woman", StringComparison.OrdinalIgnoreCase) ||
                Gender.Equals("Girl", StringComparison.OrdinalIgnoreCase))
                return GenderPresentation.Female;

            if (Gender.Equals("Androgynous", StringComparison.OrdinalIgnoreCase))
                return GenderPresentation.Androgynous;

            if (Gender.Equals("NonBinary", StringComparison.OrdinalIgnoreCase) ||
                Gender.Equals("Non-Binary", StringComparison.OrdinalIgnoreCase) ||
                Gender.Equals("NB", StringComparison.OrdinalIgnoreCase))
                return GenderPresentation.NonBinary;

            return GenderPresentation.Unspecified;
        }
    }
}
