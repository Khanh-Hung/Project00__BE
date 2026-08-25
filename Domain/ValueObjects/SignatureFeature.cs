using Domain.Enums;

namespace Domain.ValueObjects;

public sealed record SignatureFeature(
    string Name,
    string PositiveTokens,
    string? NegativeTokens = null,
    FeatureImportance Importance = FeatureImportance.Critical,
    FeaturePersistence Persistence = FeaturePersistence.EveryTurn
);
