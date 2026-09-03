using Domain.Enums;

namespace Domain.ValueObjects;

public sealed record CharacterStateSnapshot
{
    public int Energy { get; init; } = 80;
    public CharacterMood Mood { get; init; } = CharacterMood.Neutral;
    public decimal MoodScalar { get; init; } = 50m;
    public int MoodIntensity { get; init; } = 20;
    public int Hunger { get; init; } = 20;
    public int SocialNeed { get; init; } = 30;
    public int Stress { get; init; } = 10;
    public int Comfort { get; init; } = 80;
    public int Fitness { get; init; } = 50;
    public int Intellect { get; init; } = 50;
    public int Confidence { get; init; } = 50;
    public DateTime? LastEvolvedAtUtc { get; init; }
    public int Version { get; init; } = 1;

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
        int confidence = 50,
        int comfort = 80,
        decimal moodScalar = 50m,
        DateTime? lastEvolvedAtUtc = null,
        int version = 1)
    {
        Energy = Math.Clamp(energy, 0, 100);
        Mood = mood;
        MoodScalar = Math.Clamp(moodScalar, 0m, 100m);
        MoodIntensity = Math.Clamp(moodIntensity, 0, 100);
        Hunger = Math.Clamp(hunger, 0, 100);
        SocialNeed = Math.Clamp(socialNeed, 0, 100);
        Stress = Math.Clamp(stress, 0, 100);
        Comfort = Math.Clamp(comfort, 0, 100);
        Fitness = Math.Clamp(fitness, 0, 100);
        Intellect = Math.Clamp(intellect, 0, 100);
        Confidence = Math.Clamp(confidence, 0, 100);
        LastEvolvedAtUtc = lastEvolvedAtUtc;
        Version = version;
    }

    /// <summary>
    /// For test fixtures and DTO scaffolding only.
    /// Do NOT use as a runtime fallback in authoritative production execution paths.
    /// </summary>
    [Obsolete("For test and DTO fixtures only. Do NOT use as runtime fallback in authoritative production code paths.", false)]
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
        int comfortDelta = 0,
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
            confidence: Confidence + confidenceDelta,
            comfort: Comfort + comfortDelta,
            moodScalar: Math.Clamp(MoodScalar + (decimal)moodIntensityDelta, 0m, 100m),
            lastEvolvedAtUtc: LastEvolvedAtUtc,
            version: Version
        );
    }
}
