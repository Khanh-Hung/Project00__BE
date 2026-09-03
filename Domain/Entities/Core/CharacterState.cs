using Domain.Common;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Entities;

/// <summary>
/// Authoritative persistent aggregate root representing the current physiological and psychological needs state of a character.
/// Strictly validates invariants [0, 100], increments optimistic concurrency Version on mutation, and tracks temporal evolution.
/// </summary>
public class CharacterState : Entity
{
    public Guid CharacterId { get; private set; }

    public decimal Hunger { get; private set; }
    public decimal Energy { get; private set; }
    public decimal Mood { get; private set; }
    public decimal Stress { get; private set; }
    public decimal SocialNeed { get; private set; }
    public decimal Comfort { get; private set; }

    public DateTime LastEvolvedAtUtc { get; private set; }
    public int Version { get; private set; }

    private CharacterState() : base() { }

    public CharacterState(
        Guid characterId,
        DateTime initializedAtUtc,
        decimal hunger = 20m,
        decimal energy = 80m,
        decimal mood = 50m,
        decimal stress = 10m,
        decimal socialNeed = 30m,
        decimal comfort = 80m) : base()
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        ValidateScalar(hunger, nameof(hunger));
        ValidateScalar(energy, nameof(energy));
        ValidateScalar(mood, nameof(mood));
        ValidateScalar(stress, nameof(stress));
        ValidateScalar(socialNeed, nameof(socialNeed));
        ValidateScalar(comfort, nameof(comfort));

        CharacterId = characterId;
        Hunger = Math.Clamp(hunger, 0m, 100m);
        Energy = Math.Clamp(energy, 0m, 100m);
        Mood = Math.Clamp(mood, 0m, 100m);
        Stress = Math.Clamp(stress, 0m, 100m);
        SocialNeed = Math.Clamp(socialNeed, 0m, 100m);
        Comfort = Math.Clamp(comfort, 0m, 100m);

        LastEvolvedAtUtc = initializedAtUtc;
        Version = 1;
        Touch();
    }

    public static CharacterState CreateDefault(Guid characterId, DateTime initializedAtUtc)
    {
        return new CharacterState(
            characterId: characterId,
            initializedAtUtc: initializedAtUtc,
            hunger: 20m,
            energy: 80m,
            mood: 50m,
            stress: 10m,
            socialNeed: 30m,
            comfort: 80m
        );
    }

    public void ApplyDelta(CharacterStateDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta, nameof(delta));

        Hunger = Math.Clamp(Hunger + delta.HungerDelta, 0m, 100m);
        Energy = Math.Clamp(Energy + delta.EnergyDelta, 0m, 100m);
        Mood = Math.Clamp(Mood + delta.MoodDelta, 0m, 100m);
        Stress = Math.Clamp(Stress + delta.StressDelta, 0m, 100m);
        SocialNeed = Math.Clamp(SocialNeed + delta.SocialNeedDelta, 0m, 100m);
        Comfort = Math.Clamp(Comfort + delta.ComfortDelta, 0m, 100m);

        Version++;
        Touch();
    }

    public void Evolve(CharacterStateDelta delta, DateTime evolvedToUtc)
    {
        ArgumentNullException.ThrowIfNull(delta, nameof(delta));

        if (evolvedToUtc < LastEvolvedAtUtc)
            throw new InvalidOperationException($"Cannot evolve character state backwards in time. Current LastEvolvedAtUtc: {LastEvolvedAtUtc:O}, target: {evolvedToUtc:O}");

        if (!delta.IsZero)
        {
            Hunger = Math.Clamp(Hunger + delta.HungerDelta, 0m, 100m);
            Energy = Math.Clamp(Energy + delta.EnergyDelta, 0m, 100m);
            Mood = Math.Clamp(Mood + delta.MoodDelta, 0m, 100m);
            Stress = Math.Clamp(Stress + delta.StressDelta, 0m, 100m);
            SocialNeed = Math.Clamp(SocialNeed + delta.SocialNeedDelta, 0m, 100m);
            Comfort = Math.Clamp(Comfort + delta.ComfortDelta, 0m, 100m);
        }

        LastEvolvedAtUtc = evolvedToUtc;
        Version++;
        Touch();
    }

    public CharacterStateSnapshot ToSnapshot()
    {
        // Deterministically map scalar Mood to CharacterMood
        CharacterMood moodEnum = Mood switch
        {
            >= 75m => CharacterMood.Happy,
            >= 45m and < 75m => CharacterMood.Neutral,
            >= 30m and < 45m => Stress > 50m ? CharacterMood.Anxious : CharacterMood.Sad,
            _ => Stress > 70m ? CharacterMood.Angry : CharacterMood.Sad
        };

        return new CharacterStateSnapshot(
            energy: (int)Math.Round(Energy, MidpointRounding.AwayFromZero),
            mood: moodEnum,
            moodIntensity: (int)Math.Round(Math.Abs(Mood - 50m) * 2m, MidpointRounding.AwayFromZero),
            hunger: (int)Math.Round(Hunger, MidpointRounding.AwayFromZero),
            socialNeed: (int)Math.Round(SocialNeed, MidpointRounding.AwayFromZero),
            stress: (int)Math.Round(Stress, MidpointRounding.AwayFromZero),
            comfort: (int)Math.Round(Comfort, MidpointRounding.AwayFromZero),
            moodScalar: Mood,
            lastEvolvedAtUtc: LastEvolvedAtUtc,
            version: Version
        );
    }

    private static void ValidateScalar(decimal value, string paramName)
    {
        if (value < 0m || value > 100m)
            throw new ArgumentOutOfRangeException(paramName, value, "Character state scalar value must be between 0 and 100.");
    }
}
