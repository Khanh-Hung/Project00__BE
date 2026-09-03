using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Policies;

/// <summary>
/// Domain policy evaluating utility adjustments and priority thresholds for autonomous decision making based on physiological and psychological needs.
/// Keeps CharacterState pure and focused on state representation.
/// </summary>
public static class CharacterNeedsDecisionPolicy
{
    public const decimal CriticalEnergyThreshold = 20m;
    public const decimal LowEnergyThreshold = 30m;

    public const decimal CriticalHungerThreshold = 80m;
    public const decimal HighHungerThreshold = 60m;

    public const decimal HighSocialNeedThreshold = 75m;
    public const decimal ModerateSocialNeedThreshold = 50m;

    public const decimal HighStressThreshold = 80m;
    public const decimal ModerateStressThreshold = 60m;

    public const decimal LowComfortThreshold = 20m;

    public static int EvaluateEnergyModifier(decimal energy, CharacterActivityType activityType)
    {
        if (energy <= CriticalEnergyThreshold)
        {
            if (activityType == CharacterActivityType.Sleeping || activityType == CharacterActivityType.Relaxing)
                return 300;
            if (activityType == CharacterActivityType.Exercising || activityType == CharacterActivityType.Working || activityType == CharacterActivityType.Exploring)
                return -250;
        }
        else if (energy <= LowEnergyThreshold)
        {
            if (activityType == CharacterActivityType.Sleeping || activityType == CharacterActivityType.Relaxing)
                return 80;
            if (activityType == CharacterActivityType.Exercising || activityType == CharacterActivityType.Working)
                return -50;
        }

        return 0;
    }

    public static int EvaluateHungerModifier(decimal hunger, CharacterActivityType activityType)
    {
        if (hunger >= CriticalHungerThreshold)
        {
            if (activityType == CharacterActivityType.Eating || activityType == CharacterActivityType.Cooking)
                return 300;
        }
        else if (hunger >= HighHungerThreshold)
        {
            if (activityType == CharacterActivityType.Eating || activityType == CharacterActivityType.Cooking)
                return 80;
        }

        return 0;
    }

    public static int EvaluateSocialNeedModifier(decimal socialNeed, CharacterActivityType activityType)
    {
        if (socialNeed >= HighSocialNeedThreshold)
        {
            if (activityType == CharacterActivityType.Socializing)
                return 250;
        }
        else if (socialNeed >= ModerateSocialNeedThreshold)
        {
            if (activityType == CharacterActivityType.Socializing)
                return 60;
        }

        return 0;
    }

    public static int EvaluateStressModifier(decimal stress, CharacterActivityType activityType)
    {
        if (stress >= HighStressThreshold)
        {
            if (activityType == CharacterActivityType.Relaxing || activityType == CharacterActivityType.Reading || activityType == CharacterActivityType.Sleeping)
                return 250;
            if (activityType == CharacterActivityType.Working)
                return -200;
        }
        else if (stress >= ModerateStressThreshold)
        {
            if (activityType == CharacterActivityType.Relaxing || activityType == CharacterActivityType.Reading)
                return 50;
        }

        return 0;
    }

    public static int EvaluateComfortModifier(decimal comfort, CharacterActivityType activityType)
    {
        if (comfort <= LowComfortThreshold)
        {
            if (activityType == CharacterActivityType.Relaxing || activityType == CharacterActivityType.Sleeping)
                return 150;
        }

        return 0;
    }
}
