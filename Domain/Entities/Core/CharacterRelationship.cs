using System;
using System.Collections.Generic;
using System.Linq;
using Domain.Common;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Entities;

/// <summary>
/// Domain entity representing Character A's persistent social relationship toward Target B.
/// Core social dimensions: Trust (0..100), Affection (0..100), Familiarity (0..100).
/// Social classification: RelationshipType (e.g. Stranger, Acquaintance, Friend, etc.).
/// Invariant: Relationship is contextual social state, NOT CharacterState or Memory.
/// </summary>
public sealed class CharacterRelationship : BaseEntity
{
    public const int MinDimensionValue = 0;
    public const int MaxDimensionValue = 100;

    public Guid CharacterId { get; private set; }
    public RelationshipTargetType TargetType { get; private set; } = RelationshipTargetType.User;
    public Guid TargetId { get; private set; }
    public RelationshipType RelationshipType { get; private set; } = RelationshipType.Stranger;

    public int Trust { get; private set; }
    public int Affection { get; private set; }
    public int Familiarity { get; private set; }

    // Retained for backward compatibility with CharacterRuntime and legacy chat sessions:
    public Guid UserId { get; private set; }
    public int AffectionScore { get; private set; }
    public CharacterMood CurrentMood { get; private set; } = CharacterMood.Neutral;
    public int MoodIntensity { get; private set; } = 20;
    public DateTime LastInteractedAt { get; private set; }
    public uint Version { get; private set; } = 1;

    private readonly List<RelationshipEvent> _events = [];
    public IReadOnlyCollection<RelationshipEvent> Events => _events.AsReadOnly();

    private CharacterRelationship() { } // EF Core

    private CharacterRelationship(
        Guid characterId,
        RelationshipTargetType targetType,
        Guid targetId,
        RelationshipType relationshipType,
        int trust,
        int affection,
        int familiarity,
        Guid userId,
        int affectionScore,
        CharacterMood mood,
        int moodIntensity,
        DateTime initialTimestamp)
    {
        CharacterId = characterId;
        TargetType = targetType;
        TargetId = targetId;
        RelationshipType = relationshipType;
        Trust = Math.Clamp(trust, MinDimensionValue, MaxDimensionValue);
        Affection = Math.Clamp(affection, MinDimensionValue, MaxDimensionValue);
        Familiarity = Math.Clamp(familiarity, MinDimensionValue, MaxDimensionValue);

        UserId = userId;
        AffectionScore = Math.Clamp(affectionScore, -100, 100);
        CurrentMood = mood;
        MoodIntensity = Math.Clamp(moodIntensity, 0, 100);
        LastInteractedAt = initialTimestamp;
        Version = 1;
    }

    /// <summary>
    /// Canonical domain factory method for PR48 relationships.
    /// Default initial state: Stranger, Trust=0, Affection=0, Familiarity=0.
    /// </summary>
    public static CharacterRelationship Create(
        Guid characterId,
        RelationshipTargetType targetType,
        Guid targetId,
        RelationshipType relationshipType = RelationshipType.Stranger,
        int trust = 0,
        int affection = 0,
        int familiarity = 0,
        DateTime? initialTimestamp = null)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));
        if (targetId == Guid.Empty)
            throw new ArgumentException("TargetId cannot be empty.", nameof(targetId));

        var timestamp = initialTimestamp ?? DateTime.UtcNow;
        var userId = targetType == RelationshipTargetType.User ? targetId : Guid.Empty;

        return new CharacterRelationship(
            characterId: characterId,
            targetType: targetType,
            targetId: targetId,
            relationshipType: relationshipType,
            trust: trust,
            affection: affection,
            familiarity: familiarity,
            userId: userId,
            affectionScore: affection,
            mood: CharacterMood.Neutral,
            moodIntensity: 20,
            initialTimestamp: timestamp);
    }

    /// <summary>
    /// Backward-compatible factory method for legacy chat runtime.
    /// </summary>
    public static CharacterRelationship Create(
        Guid characterId,
        Guid userId,
        int initialAffection = 0,
        CharacterMood initialMood = CharacterMood.Neutral,
        int initialMoodIntensity = 20,
        DateTime? initialTimestamp = null)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId cannot be empty.", nameof(userId));

        var timestamp = initialTimestamp ?? DateTime.UtcNow;
        var boundedAffection = Math.Clamp(initialAffection >= 0 ? initialAffection : 0, MinDimensionValue, MaxDimensionValue);

        return new CharacterRelationship(
            characterId: characterId,
            targetType: RelationshipTargetType.User,
            targetId: userId,
            relationshipType: RelationshipType.Stranger,
            trust: 0,
            affection: boundedAffection,
            familiarity: 0,
            userId: userId,
            affectionScore: initialAffection,
            mood: initialMood,
            moodIntensity: initialMoodIntensity,
            initialTimestamp: timestamp);
    }

    public (int OldValue, int NewValue, int ActualDelta) ApplyTrustDelta(int delta, DateTime? utcNow = null)
    {
        var oldValue = Trust;
        Trust = Math.Clamp(Trust + delta, MinDimensionValue, MaxDimensionValue);
        var actualDelta = Trust - oldValue;
        LastInteractedAt = utcNow ?? DateTime.UtcNow;
        Version++;
        Touch();
        return (oldValue, Trust, actualDelta);
    }

    public void IncreaseTrust(int amount, DateTime? utcNow = null)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be non-negative.");
        ApplyTrustDelta(amount, utcNow);
    }

    public void DecreaseTrust(int amount, DateTime? utcNow = null)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be non-negative.");
        ApplyTrustDelta(-amount, utcNow);
    }

    public (int OldScore, int NewScore, int ActualDelta) ApplyAffectionDelta(int delta, DateTime? utcNow = null)
    {
        var oldScore = AffectionScore;
        AffectionScore = Math.Clamp(AffectionScore + delta, -100, 100);
        Affection = Math.Clamp(Affection + delta, MinDimensionValue, MaxDimensionValue);
        var actualDelta = AffectionScore - oldScore;
        LastInteractedAt = utcNow ?? DateTime.UtcNow;
        Version++;
        Touch();
        return (oldScore, AffectionScore, actualDelta);
    }

    public void IncreaseAffection(int amount, DateTime? utcNow = null)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be non-negative.");
        ApplyAffectionDelta(amount, utcNow);
    }

    public void DecreaseAffection(int amount, DateTime? utcNow = null)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be non-negative.");
        ApplyAffectionDelta(-amount, utcNow);
    }

    public (int OldValue, int NewValue, int ActualDelta) ApplyFamiliarityDelta(int delta, DateTime? utcNow = null)
    {
        var oldValue = Familiarity;
        Familiarity = Math.Clamp(Familiarity + delta, MinDimensionValue, MaxDimensionValue);
        var actualDelta = Familiarity - oldValue;
        LastInteractedAt = utcNow ?? DateTime.UtcNow;
        Version++;
        Touch();
        return (oldValue, Familiarity, actualDelta);
    }

    public void IncreaseFamiliarity(int amount, DateTime? utcNow = null)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be non-negative.");
        ApplyFamiliarityDelta(amount, utcNow);
    }

    public void DecreaseFamiliarity(int amount, DateTime? utcNow = null)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be non-negative.");
        ApplyFamiliarityDelta(-amount, utcNow);
    }

    public void ChangeRelationshipType(RelationshipType newType, DateTime? utcNow = null)
    {
        RelationshipType = newType;
        LastInteractedAt = utcNow ?? DateTime.UtcNow;
        Version++;
        Touch();
    }

    public void UpdateMood(CharacterMood mood, int intensity, DateTime? utcNow = null)
    {
        CurrentMood = mood;
        MoodIntensity = Math.Clamp(intensity, 0, 100);
        LastInteractedAt = utcNow ?? DateTime.UtcNow;
        Version++;
        Touch();
    }

    public bool TryUnlockEvent(string eventKey, string context, DateTime? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(eventKey) || string.IsNullOrWhiteSpace(context))
        {
            return false;
        }

        var trimmedKey = eventKey.Trim();
        var trimmedContext = context.Trim();

        if (trimmedKey.Length > RelationshipEvent.MaxEventKeyLength || trimmedContext.Length > RelationshipEvent.MaxContextLength)
        {
            return false;
        }

        if (_events.Any(e => string.Equals(e.EventKey, trimmedKey, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var timestamp = utcNow ?? DateTime.UtcNow;
        _events.Add(new RelationshipEvent(trimmedKey, trimmedContext, timestamp));
        LastInteractedAt = timestamp;
        Version++;
        Touch();
        return true;
    }

    public void SoftenMoodIfInactive(DateTime utcNow, TimeSpan inactiveThreshold, CharacterMood defaultMood = CharacterMood.Neutral)
    {
        if (utcNow - LastInteractedAt > inactiveThreshold)
        {
            CurrentMood = defaultMood;
            MoodIntensity = 20;
            Version++;
            Touch();
        }
    }
}
