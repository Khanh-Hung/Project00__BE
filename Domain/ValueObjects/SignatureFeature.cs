using Domain.Enums;

namespace Domain.ValueObjects;

public sealed record SignatureFeature(
    string Name,
    string PositiveTokens,
    string? NegativeTokens = null,
    FeatureImportance Importance = FeatureImportance.Critical,
    FeaturePersistence Persistence = FeaturePersistence.EveryTurn
)
{
    public bool ShouldInject(bool isSameScene = true)
    {
        if (Persistence == FeaturePersistence.EveryTurn)
            return true;

        if (Persistence == FeaturePersistence.SameSceneOnly && isSameScene)
            return true;

        return Importance == FeatureImportance.Critical;
    }
}
