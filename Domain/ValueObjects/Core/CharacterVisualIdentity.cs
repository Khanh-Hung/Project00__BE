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
    IReadOnlyList<SignatureFeature>? SignatureFeatures = null,
    string? Style = null,
    VisualStyle VisualStyle = VisualStyle.Unspecified
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

    public VisualStyle ResolvedStyle
    {
        get
        {
            if (VisualStyle != VisualStyle.Unspecified)
                return VisualStyle;

            if (string.IsNullOrWhiteSpace(Style))
                return VisualStyle.Unspecified;

            if (Enum.TryParse<VisualStyle>(Style, ignoreCase: true, out var parsed))
                return parsed;

            if (Style.Equals("3D", StringComparison.OrdinalIgnoreCase) ||
                Style.Equals("ThreeDimensional", StringComparison.OrdinalIgnoreCase) ||
                Style.Equals("3D Render", StringComparison.OrdinalIgnoreCase))
                return VisualStyle.ThreeDimensional;

            if (Style.Equals("Semi-Realistic", StringComparison.OrdinalIgnoreCase) ||
                Style.Equals("SemiRealistic", StringComparison.OrdinalIgnoreCase))
                return VisualStyle.SemiRealistic;

            if (Style.Equals("Pixel Art", StringComparison.OrdinalIgnoreCase) ||
                Style.Equals("PixelArt", StringComparison.OrdinalIgnoreCase))
                return VisualStyle.PixelArt;

            return VisualStyle.Unspecified;
        }
    }

    /// <summary>
    /// Creates a model-agnostic identity conditioning intent from this character visual identity.
    /// Expresses WHAT identity preservation is required without model or infrastructure dependencies.
    /// </summary>
    public IdentityConditioningIntent CreateConditioningIntent(
        string? previousSceneReferenceUrl = null,
        float? preservationStrength = null,
        Slot2Context context = Slot2Context.ColdStart,
        Slot2ConditioningMode continuityMode = Slot2ConditioningMode.SceneStyleContinuity)
    {
        var referenceUrl = !string.IsNullOrWhiteSpace(CanonicalReferenceUrl)
            ? CanonicalReferenceUrl
            : (!string.IsNullOrWhiteSpace(FullBodyUrl) ? FullBodyUrl : null);

        return IdentityConditioningIntent.FromReferences(
            canonicalReferenceUrl: referenceUrl,
            previousSceneReferenceUrl: previousSceneReferenceUrl,
            preservationStrength: preservationStrength,
            context: context,
            continuityMode: continuityMode
        );
    }
}
