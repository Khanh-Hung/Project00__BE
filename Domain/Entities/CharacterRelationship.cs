using Domain.Common;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Entities;

public sealed class CharacterRelationship : BaseEntity
{
    public Guid CharacterId { get; private set; }
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
        Guid userId,
        int initialAffection,
        CharacterMood initialMood,
        int initialMoodIntensity,
        DateTime initialTimestamp)
    {
        CharacterId = characterId;
        UserId = userId;
        AffectionScore = Math.Clamp(initialAffection, -100, 100);
        CurrentMood = initialMood;
        MoodIntensity = Math.Clamp(initialMoodIntensity, 0, 100);
        LastInteractedAt = initialTimestamp;
        Version = 1;
    }

    public static CharacterRelationship Create(
        Guid characterId,
        Guid userId,
        int initialAffection = 0,
        CharacterMood initialMood = CharacterMood.Neutral,
        int initialMoodIntensity = 20,
        DateTime? initialTimestamp = null)
    {
        if (characterId == Guid.Empty)
        {
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("UserId cannot be empty.", nameof(userId));
        }

        var timestamp = initialTimestamp ?? DateTime.UtcNow;

        return new CharacterRelationship(
            characterId,
            userId,
            initialAffection,
            initialMood,
            initialMoodIntensity,
            timestamp);
    }

    public (int OldScore, int NewScore, int ActualDelta) ApplyAffectionDelta(int delta, DateTime? utcNow = null)
    {
        var oldScore = AffectionScore;
        AffectionScore = Math.Clamp(AffectionScore + delta, -100, 100);
        var actualDelta = AffectionScore - oldScore;
        LastInteractedAt = utcNow ?? DateTime.UtcNow;
        Version++;
        Touch();
        return (oldScore, AffectionScore, actualDelta);
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

        // Reject invalid lengths per domain invariant
        if (trimmedKey.Length > RelationshipEvent.MaxEventKeyLength || trimmedContext.Length > RelationshipEvent.MaxContextLength)
        {
            return false;
        }

        // Deduplicate by EventKey (case-insensitive)
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
