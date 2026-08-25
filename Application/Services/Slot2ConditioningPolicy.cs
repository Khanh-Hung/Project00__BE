using System.Globalization;
using Domain.ValueObjects;
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

        var sameWeightStr = configuration["AiProviders:ImageGeneration:Slot2Policy:SameSceneWeight"]
            ?? configuration["AiProviders:ImageGeneration:SceneContinuity:SameSceneWeight"]
            ?? configuration["AiProviders:ImageGeneration:SceneContinuity:Weight"];
        double sameWeight = ParseValidatedUnitInterval(
            "AiProviders:ImageGeneration:Slot2Policy:SameSceneWeight",
            sameWeightStr,
            Default.SameSceneWeight);

        var sameEndAtStr = configuration["AiProviders:ImageGeneration:Slot2Policy:SameSceneEndAt"]
            ?? configuration["AiProviders:ImageGeneration:SceneContinuity:SameSceneEndAt"]
            ?? configuration["AiProviders:ImageGeneration:SceneContinuity:EndAt"];
        double sameEndAt = ParseValidatedUnitInterval(
            "AiProviders:ImageGeneration:Slot2Policy:SameSceneEndAt",
            sameEndAtStr,
            Default.SameSceneEndAt);

        var transWeightStr = configuration["AiProviders:ImageGeneration:Slot2Policy:TransitionWeight"]
            ?? configuration["AiProviders:ImageGeneration:SceneContinuity:TransitionWeight"];
        double transWeight = ParseValidatedUnitInterval(
            "AiProviders:ImageGeneration:Slot2Policy:TransitionWeight",
            transWeightStr,
            Default.TransitionWeight);

        var transEndAtStr = configuration["AiProviders:ImageGeneration:Slot2Policy:TransitionEndAt"]
            ?? configuration["AiProviders:ImageGeneration:SceneContinuity:TransitionEndAt"];
        double transEndAt = ParseValidatedUnitInterval(
            "AiProviders:ImageGeneration:Slot2Policy:TransitionEndAt",
            transEndAtStr,
            Default.TransitionEndAt);

        var bypassStr = configuration["AiProviders:ImageGeneration:Slot2Policy:BypassOnColdStart"];
        bool bypassColdStart = ParseValidatedBool(
            "AiProviders:ImageGeneration:Slot2Policy:BypassOnColdStart",
            bypassStr,
            Default.BypassOnColdStart);

        return new Slot2ConditioningPolicy(
            SameSceneWeight: sameWeight,
            SameSceneEndAt: sameEndAt,
            TransitionWeight: transWeight,
            TransitionEndAt: transEndAt,
            BypassOnColdStart: bypassColdStart
        );
    }

    private static double ParseValidatedUnitInterval(string keyName, string? valueStr, double defaultValue)
    {
        if (string.IsNullOrWhiteSpace(valueStr))
            return defaultValue;

        if (!double.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed)
            || double.IsInfinity(parsed)
            || parsed < 0.0
            || parsed > 1.0)
        {
            throw new InvalidOperationException(
                $"Invalid configuration for '{keyName}': '{valueStr}'. Value must be a valid number between 0.0 and 1.0.");
        }

        return parsed;
    }

    private static bool ParseValidatedBool(string keyName, string? valueStr, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(valueStr))
            return defaultValue;

        if (!bool.TryParse(valueStr, out var parsed))
        {
            throw new InvalidOperationException(
                $"Invalid configuration for '{keyName}': '{valueStr}'. Value must be a boolean ('true' or 'false').");
        }

        return parsed;
    }

    public Slot2ConditioningDecision Decide(bool isColdStart, bool isTransition)
    {
        if (isColdStart && BypassOnColdStart)
        {
            return new Slot2ConditioningDecision(
                IsActive: false,
                Weight: 0.0f,
                EndAt: 0.0f,
                Context: Domain.Enums.Slot2Context.ColdStart
            );
        }

        if (isTransition)
        {
            return new Slot2ConditioningDecision(
                IsActive: true,
                Weight: (float)Math.Clamp(TransitionWeight, 0.0, 1.0),
                EndAt: (float)Math.Clamp(TransitionEndAt, 0.0, 1.0),
                Context: Domain.Enums.Slot2Context.SceneTransition
            );
        }

        return new Slot2ConditioningDecision(
            IsActive: true,
            Weight: (float)Math.Clamp(SameSceneWeight, 0.0, 1.0),
            EndAt: (float)Math.Clamp(SameSceneEndAt, 0.0, 1.0),
            Context: Domain.Enums.Slot2Context.SameScene
        );
    }

    public (double Weight, double EndAt, bool IsActive) Resolve(bool isColdStart, bool isTransition)
    {
        var decision = Decide(isColdStart, isTransition);
        return (decision.Weight, decision.EndAt, decision.IsActive);
    }
}
