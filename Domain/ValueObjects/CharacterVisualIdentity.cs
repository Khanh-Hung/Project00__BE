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
    string? FullBodyUrl = null
);
