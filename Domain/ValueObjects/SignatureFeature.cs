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
    public bool ShouldInject(Slot2Context context)
    {
        return Persistence switch
        {
            FeaturePersistence.EveryTurn => true,
            FeaturePersistence.SameSceneOnly => context == Slot2Context.SameScene,
            _ => Importance == FeatureImportance.Critical && context != Slot2Context.SceneTransition
        };
    }
}
