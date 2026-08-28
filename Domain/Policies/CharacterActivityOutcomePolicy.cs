using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Policies;

public sealed record ActivityStateDelta(
    int EnergyDelta,
    int HungerDelta,
    int SocialNeedDelta,
    int StressDelta,
    int FitnessDelta = 0,
    int IntellectDelta = 0,
    int MoodIntensityDelta = 0,
    CharacterMood? ResultingMood = null
);

public static class CharacterActivityOutcomePolicy
{
    public static ActivityStateDelta CalculateDelta(CharacterActivityType activityType)
    {
        return activityType switch
        {
            CharacterActivityType.Sleeping => new ActivityStateDelta(
                EnergyDelta: 60,
                HungerDelta: 20,
                SocialNeedDelta: 0,
                StressDelta: -30,
                MoodIntensityDelta: 10,
                ResultingMood: CharacterMood.Neutral
            ),

            CharacterActivityType.Eating => new ActivityStateDelta(
                EnergyDelta: 15,
                HungerDelta: -50,
                SocialNeedDelta: -5,
                StressDelta: -10,
                MoodIntensityDelta: 15,
                ResultingMood: CharacterMood.Happy
            ),

            CharacterActivityType.Cooking => new ActivityStateDelta(
                EnergyDelta: -10,
                HungerDelta: -25,
                SocialNeedDelta: -5,
                StressDelta: -5,
                IntellectDelta: 2,
                MoodIntensityDelta: 10,
                ResultingMood: CharacterMood.Happy
            ),

            CharacterActivityType.Relaxing => new ActivityStateDelta(
                EnergyDelta: 20,
                HungerDelta: 5,
                SocialNeedDelta: 0,
                StressDelta: -25,
                MoodIntensityDelta: 10,
                ResultingMood: CharacterMood.Neutral
            ),

            CharacterActivityType.Exercising => new ActivityStateDelta(
                EnergyDelta: -30,
                HungerDelta: 20,
                SocialNeedDelta: -5,
                StressDelta: -20,
                FitnessDelta: 10,
                MoodIntensityDelta: 20,
                ResultingMood: CharacterMood.Happy
            ),

            CharacterActivityType.Working => new ActivityStateDelta(
                EnergyDelta: -20,
                HungerDelta: 15,
                SocialNeedDelta: 5,
                StressDelta: 10,
                IntellectDelta: 10,
                MoodIntensityDelta: 5
            ),

            CharacterActivityType.Reading => new ActivityStateDelta(
                EnergyDelta: -10,
                HungerDelta: 5,
                SocialNeedDelta: 0,
                StressDelta: -15,
                IntellectDelta: 10,
                MoodIntensityDelta: 10,
                ResultingMood: CharacterMood.Neutral
            ),

            CharacterActivityType.Socializing => new ActivityStateDelta(
                EnergyDelta: -10,
                HungerDelta: 10,
                SocialNeedDelta: -40,
                StressDelta: -15,
                MoodIntensityDelta: 25,
                ResultingMood: CharacterMood.Happy
            ),

            CharacterActivityType.Exploring => new ActivityStateDelta(
                EnergyDelta: -25,
                HungerDelta: 15,
                SocialNeedDelta: -10,
                StressDelta: -10,
                FitnessDelta: 5,
                IntellectDelta: 5,
                MoodIntensityDelta: 20,
                ResultingMood: CharacterMood.Happy
            ),

            CharacterActivityType.Bathing => new ActivityStateDelta(
                EnergyDelta: 10,
                HungerDelta: 5,
                SocialNeedDelta: 0,
                StressDelta: -30,
                MoodIntensityDelta: 15,
                ResultingMood: CharacterMood.Neutral
            ),

            CharacterActivityType.GettingReady => new ActivityStateDelta(
                EnergyDelta: -5,
                HungerDelta: 5,
                SocialNeedDelta: 0,
                StressDelta: -5,
                MoodIntensityDelta: 10,
                ResultingMood: CharacterMood.Neutral
            ),

            CharacterActivityType.Walking => new ActivityStateDelta(
                EnergyDelta: -10,
                HungerDelta: 10,
                SocialNeedDelta: -5,
                StressDelta: -15,
                FitnessDelta: 5,
                MoodIntensityDelta: 10,
                ResultingMood: CharacterMood.Neutral
            ),

            _ => new ActivityStateDelta(
                EnergyDelta: 5,
                HungerDelta: 5,
                SocialNeedDelta: 0,
                StressDelta: -5,
                MoodIntensityDelta: 0,
                ResultingMood: null
            )
        };
    }

    public static CharacterStateSnapshot ApplyOutcome(CharacterStateSnapshot current, CharacterActivityType activityType)
    {
        var delta = CalculateDelta(activityType);
        return current.ApplyDelta(
            energyDelta: delta.EnergyDelta,
            hungerDelta: delta.HungerDelta,
            socialNeedDelta: delta.SocialNeedDelta,
            stressDelta: delta.StressDelta,
            fitnessDelta: delta.FitnessDelta,
            intellectDelta: delta.IntellectDelta,
            moodIntensityDelta: delta.MoodIntensityDelta,
            newMood: delta.ResultingMood
        );
    }
}
