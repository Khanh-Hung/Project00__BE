using System;
using Domain.Common;

namespace Domain.Entities;

/// <summary>
/// Authoritative domain aggregate representing long-term personality traits of a character.
/// Strictly decoupled from transient physiological/mental condition (CharacterState).
/// Traits are bounded [0, 100] and mutate gradually via evidence accumulation.
/// Optimistic concurrency token (Version) prevents lost updates across concurrent workers.
/// </summary>
public sealed class CharacterPersonality : Entity
{
    public const int MinTraitValue = 0;
    public const int MaxTraitValue = 100;
    public const int DefaultTraitValue = 50;
    public const int MaxAdaptationStep = 1;

    public Guid CharacterId { get; private set; }

    public int Warmth { get; private set; }
    public int Openness { get; private set; }
    public int Assertiveness { get; private set; }
    public int Conscientiousness { get; private set; }
    public int SocialConfidence { get; private set; }
    public int TrustDisposition { get; private set; }
    public int EmotionalStability { get; private set; }

    public uint Version { get; private set; } = 1;

    private CharacterPersonality() : base() { } // EF Core

    public CharacterPersonality(
        Guid characterId,
        int warmth = DefaultTraitValue,
        int openness = DefaultTraitValue,
        int assertiveness = DefaultTraitValue,
        int conscientiousness = DefaultTraitValue,
        int socialConfidence = DefaultTraitValue,
        int trustDisposition = DefaultTraitValue,
        int emotionalStability = DefaultTraitValue,
        uint version = 1,
        DateTime? createdAtUtc = null) : base()
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        CharacterId = characterId;
        Warmth = Math.Clamp(warmth, MinTraitValue, MaxTraitValue);
        Openness = Math.Clamp(openness, MinTraitValue, MaxTraitValue);
        Assertiveness = Math.Clamp(assertiveness, MinTraitValue, MaxTraitValue);
        Conscientiousness = Math.Clamp(conscientiousness, MinTraitValue, MaxTraitValue);
        SocialConfidence = Math.Clamp(socialConfidence, MinTraitValue, MaxTraitValue);
        TrustDisposition = Math.Clamp(trustDisposition, MinTraitValue, MaxTraitValue);
        EmotionalStability = Math.Clamp(emotionalStability, MinTraitValue, MaxTraitValue);
        Version = version == 0 ? 1u : version;

        if (createdAtUtc.HasValue)
        {
            CreatedAt = createdAtUtc.Value;
            UpdatedAt = createdAtUtc.Value;
        }
    }

    public static CharacterPersonality CreateDefault(Guid characterId, DateTime? createdAtUtc = null)
    {
        return new CharacterPersonality(
            characterId: characterId,
            warmth: DefaultTraitValue,
            openness: DefaultTraitValue,
            assertiveness: DefaultTraitValue,
            conscientiousness: DefaultTraitValue,
            socialConfidence: DefaultTraitValue,
            trustDisposition: DefaultTraitValue,
            emotionalStability: DefaultTraitValue,
            version: 1,
            createdAtUtc: createdAtUtc ?? DateTime.UtcNow);
    }

    public int GetTrait(string traitKey)
    {
        var normalized = PersonalityTraitKeys.Normalize(traitKey);
        return normalized switch
        {
            PersonalityTraitKeys.Warmth => Warmth,
            PersonalityTraitKeys.Openness => Openness,
            PersonalityTraitKeys.Assertiveness => Assertiveness,
            PersonalityTraitKeys.Conscientiousness => Conscientiousness,
            PersonalityTraitKeys.SocialConfidence => SocialConfidence,
            PersonalityTraitKeys.TrustDisposition => TrustDisposition,
            PersonalityTraitKeys.EmotionalStability => EmotionalStability,
            _ => throw new ArgumentException($"Unsupported trait key: '{traitKey}'.", nameof(traitKey))
        };
    }

    /// <summary>
    /// Applies a bounded gradual adaptation delta (max +/-1 point) to the specified trait.
    /// Trait values remain strictly clamped within [0, 100].
    /// Version is monotonically advanced.
    /// </summary>
    public (int ValueBefore, int ValueAfter) AdaptTrait(string traitKey, int delta)
    {
        var normalized = PersonalityTraitKeys.Normalize(traitKey);

        if (Math.Abs(delta) > MaxAdaptationStep)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delta), delta,
                $"Gradual personality adaptation cannot exceed +/-{MaxAdaptationStep} trait point per step. Requested: {delta}.");
        }

        if (delta == 0)
        {
            var current = GetTrait(normalized);
            return (current, current);
        }

        int valueBefore;
        int valueAfter;

        switch (normalized)
        {
            case PersonalityTraitKeys.Warmth:
                valueBefore = Warmth;
                Warmth = Math.Clamp(Warmth + delta, MinTraitValue, MaxTraitValue);
                valueAfter = Warmth;
                break;
            case PersonalityTraitKeys.Openness:
                valueBefore = Openness;
                Openness = Math.Clamp(Openness + delta, MinTraitValue, MaxTraitValue);
                valueAfter = Openness;
                break;
            case PersonalityTraitKeys.Assertiveness:
                valueBefore = Assertiveness;
                Assertiveness = Math.Clamp(Assertiveness + delta, MinTraitValue, MaxTraitValue);
                valueAfter = Assertiveness;
                break;
            case PersonalityTraitKeys.Conscientiousness:
                valueBefore = Conscientiousness;
                Conscientiousness = Math.Clamp(Conscientiousness + delta, MinTraitValue, MaxTraitValue);
                valueAfter = Conscientiousness;
                break;
            case PersonalityTraitKeys.SocialConfidence:
                valueBefore = SocialConfidence;
                SocialConfidence = Math.Clamp(SocialConfidence + delta, MinTraitValue, MaxTraitValue);
                valueAfter = SocialConfidence;
                break;
            case PersonalityTraitKeys.TrustDisposition:
                valueBefore = TrustDisposition;
                TrustDisposition = Math.Clamp(TrustDisposition + delta, MinTraitValue, MaxTraitValue);
                valueAfter = TrustDisposition;
                break;
            case PersonalityTraitKeys.EmotionalStability:
                valueBefore = EmotionalStability;
                EmotionalStability = Math.Clamp(EmotionalStability + delta, MinTraitValue, MaxTraitValue);
                valueAfter = EmotionalStability;
                break;
            default:
                throw new ArgumentException($"Unsupported trait key: '{traitKey}'.", nameof(traitKey));
        }

        Version = checked(Version + 1u);
        Touch();

        return (valueBefore, valueAfter);
    }
}
