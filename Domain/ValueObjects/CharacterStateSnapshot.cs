using Domain.Enums;

namespace Domain.ValueObjects;

public sealed record CharacterStateSnapshot
{
    public int Energy { get; init; } = 80;
    public CharacterMood Mood { get; init; } = CharacterMood.Neutral;
    public int MoodIntensity { get; init; } = 20;
    public int Hunger { get; init; } = 20;
    public int SocialNeed { get; init; } = 30;
    public int Stress { get; init; } = 10;
    public int Fitness { get; init; } = 50;
    public int Intellect { get; init; } = 50;
    public int Confidence { get; init; } = 50;

    public CharacterStateSnapshot() { }

    public CharacterStateSnapshot(
        int energy = 80,
        CharacterMood mood = CharacterMood.Neutral,
        int moodIntensity = 20,
        int hunger = 20,
        int socialNeed = 30,
        int stress = 10,
        int fitness = 50,
        int intellect = 50,
        int confidence = 50)
    {
        Energy = Math.Clamp(energy, 0, 100);
        Mood = mood;
        MoodIntensity = Math.Clamp(moodIntensity, 0, 100);
        Hunger = Math.Clamp(hunger, 0, 100);
        SocialNeed = Math.Clamp(socialNeed, 0, 100);
        Stress = Math.Clamp(stress, 0, 100);
        Fitness = Math.Clamp(fitness, 0, 100);
        Intellect = Math.Clamp(intellect, 0, 100);
        Confidence = Math.Clamp(confidence, 0, 100);
    }

    public static CharacterStateSnapshot CreateDefault() => new();

    public CharacterStateSnapshot ApplyDelta(
        int energyDelta = 0,
        int hungerDelta = 0,
        int socialNeedDelta = 0,
        int stressDelta = 0,
        int fitnessDelta = 0,
        int intellectDelta = 0,
        int moodIntensityDelta = 0,
        int confidenceDelta = 0,
        CharacterMood? newMood = null)
    {
        return new CharacterStateSnapshot(
            energy: Energy + energyDelta,
            mood: newMood ?? Mood,
            moodIntensity: MoodIntensity + moodIntensityDelta,
            hunger: Hunger + hungerDelta,
            socialNeed: SocialNeed + socialNeedDelta,
            stress: Stress + stressDelta,
            fitness: Fitness + fitnessDelta,
            intellect: Intellect + intellectDelta,
            confidence: Confidence + confidenceDelta
        );
    }
}
