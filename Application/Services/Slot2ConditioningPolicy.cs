using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Application.Services;

/// <summary>
/// Configurable policy abstraction for Slot 2 (Previous Scene) conditioning parameters.
/// Allows fine-tuned control over continuity versus action compliance without hardcoded magic numbers.
/// </summary>
public sealed record Slot2ConditioningPolicy(
    double SameSceneWeight = 0.15,
    double SameSceneEndAt = 0.30,
    double TransitionWeight = 0.08,
    double TransitionEndAt = 0.20,
    bool BypassOnColdStart = true
)
{
    public static readonly Slot2ConditioningPolicy Default = new();

    public static Slot2ConditioningPolicy FromConfiguration(IConfiguration? configuration)
    {
        if (configuration == null) return Default;

        double sameWeight = Default.SameSceneWeight;
        double sameEndAt = Default.SameSceneEndAt;
        double transWeight = Default.TransitionWeight;
        double transEndAt = Default.TransitionEndAt;
        bool bypassColdStart = Default.BypassOnColdStart;

        var sameWeightStr = configuration["AiProviders:ImageGeneration:Slot2Policy:SameSceneWeight"]
            ?? configuration["AiProviders:ImageGeneration:SceneContinuity:SameSceneWeight"]
            ?? configuration["AiProviders:ImageGeneration:SceneContinuity:Weight"];
        if (double.TryParse(sameWeightStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSameWeight))
            sameWeight = Math.Clamp(parsedSameWeight, 0.0, 1.0);

        var sameEndAtStr = configuration["AiProviders:ImageGeneration:Slot2Policy:SameSceneEndAt"]
            ?? configuration["AiProviders:ImageGeneration:SceneContinuity:SameSceneEndAt"]
            ?? configuration["AiProviders:ImageGeneration:SceneContinuity:EndAt"];
        if (double.TryParse(sameEndAtStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSameEndAt))
            sameEndAt = Math.Clamp(parsedSameEndAt, 0.0, 1.0);

        var transWeightStr = configuration["AiProviders:ImageGeneration:Slot2Policy:TransitionWeight"]
            ?? configuration["AiProviders:ImageGeneration:SceneContinuity:TransitionWeight"];
        if (double.TryParse(transWeightStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedTransWeight))
            transWeight = Math.Clamp(parsedTransWeight, 0.0, 1.0);

        var transEndAtStr = configuration["AiProviders:ImageGeneration:Slot2Policy:TransitionEndAt"]
            ?? configuration["AiProviders:ImageGeneration:SceneContinuity:TransitionEndAt"];
        if (double.TryParse(transEndAtStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedTransEndAt))
            transEndAt = Math.Clamp(parsedTransEndAt, 0.0, 1.0);

        var bypassStr = configuration["AiProviders:ImageGeneration:Slot2Policy:BypassOnColdStart"];
        if (bool.TryParse(bypassStr, out var parsedBypass))
            bypassColdStart = parsedBypass;

        return new Slot2ConditioningPolicy(
            SameSceneWeight: sameWeight,
            SameSceneEndAt: sameEndAt,
            TransitionWeight: transWeight,
            TransitionEndAt: transEndAt,
            BypassOnColdStart: bypassColdStart
        );
    }

    public (double Weight, double EndAt, bool IsActive) Resolve(bool isColdStart, bool isTransition)
    {
        if (isColdStart && BypassOnColdStart)
        {
            return (0.0, 0.0, false);
        }

        if (isTransition)
        {
            return (TransitionWeight, TransitionEndAt, true);
        }

        return (SameSceneWeight, SameSceneEndAt, true);
    }
}
