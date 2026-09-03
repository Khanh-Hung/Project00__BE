using Domain.Enums;

namespace Domain.ValueObjects;

public sealed record HungerPerception(HungerLevel Level, PerceptionIntensity Intensity, decimal RawValue);

public sealed record EnergyPerception(EnergyLevel Level, PerceptionIntensity Intensity, decimal RawValue);

public sealed record StressPerception(StressLevel Level, PerceptionIntensity Intensity, decimal RawValue);

public sealed record SocialNeedPerception(SocialNeedLevel Level, PerceptionIntensity Intensity, decimal RawValue);

public sealed record ComfortPerception(ComfortLevel Level, PerceptionIntensity Intensity, decimal RawValue);

public sealed record MoodPerception(
    MoodPerceptionLevel Level,
    PerceptionIntensity Intensity,
    decimal RawValue,
    CharacterMood SemanticMood = CharacterMood.Neutral
);

public sealed record CharacterInternalExperience(
    Guid CharacterId,
    int StateVersion,
    DateTime EvaluatedAtUtc,
    HungerPerception Hunger,
    EnergyPerception Energy,
    MoodPerception Mood,
    StressPerception Stress,
    SocialNeedPerception SocialNeed,
    ComfortPerception Comfort,
    DominantNeed DominantNeed
)
{
    public CharacterInternalExperienceSnapshot ToSnapshot() =>
        new(CharacterId, StateVersion, EvaluatedAtUtc, Hunger, Energy, Mood, Stress, SocialNeed, Comfort, DominantNeed);
}

public sealed record CharacterInternalExperienceSnapshot(
    Guid CharacterId,
    int StateVersion,
    DateTime EvaluatedAtUtc,
    HungerPerception Hunger,
    EnergyPerception Energy,
    MoodPerception Mood,
    StressPerception Stress,
    SocialNeedPerception SocialNeed,
    ComfortPerception Comfort,
    DominantNeed DominantNeed
);
