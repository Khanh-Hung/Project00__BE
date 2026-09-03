using Domain.Common;
using Domain.ValueObjects;

namespace Domain.Entities;

/// <summary>
/// Authoritative state transition ledger entry.
/// Guarantees idempotency via database unique constraint on (CharacterId, ExecutionId)
/// and payload consistency verification via TransitionFingerprint.
/// </summary>
public class CharacterStateTransition : Entity
{
    public Guid CharacterId { get; private set; }
    public Guid ExecutionId { get; private set; }
    public string SourceType { get; private set; } = string.Empty;
    public string? SourceId { get; private set; }
    public string TransitionFingerprint { get; private set; } = string.Empty;

    public decimal HungerDelta { get; private set; }
    public decimal EnergyDelta { get; private set; }
    public decimal MoodDelta { get; private set; }
    public decimal StressDelta { get; private set; }
    public decimal SocialNeedDelta { get; private set; }
    public decimal ComfortDelta { get; private set; }

    public int VersionBefore { get; private set; }
    public int VersionAfter { get; private set; }
    public DateTime AppliedAtUtc { get; private set; }

    private CharacterStateTransition() : base() { }

    public CharacterStateTransition(
        Guid characterId,
        Guid executionId,
        string sourceType,
        string? sourceId,
        CharacterStateDelta delta,
        int versionBefore,
        int versionAfter,
        DateTime appliedAtUtc) : base()
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));
        if (executionId == Guid.Empty)
            throw new ArgumentException("ExecutionId cannot be empty.", nameof(executionId));
        if (string.IsNullOrWhiteSpace(sourceType))
            throw new ArgumentException("SourceType cannot be empty.", nameof(sourceType));
        ArgumentNullException.ThrowIfNull(delta, nameof(delta));

        CharacterId = characterId;
        ExecutionId = executionId;
        SourceType = sourceType.Trim();
        SourceId = sourceId?.Trim();
        TransitionFingerprint = CanonicalTransitionFingerprint.Compute(
            characterId, executionId, SourceType, SourceId, delta);

        HungerDelta = delta.HungerDelta;
        EnergyDelta = delta.EnergyDelta;
        MoodDelta = delta.MoodDelta;
        StressDelta = delta.StressDelta;
        SocialNeedDelta = delta.SocialNeedDelta;
        ComfortDelta = delta.ComfortDelta;

        VersionBefore = versionBefore;
        VersionAfter = versionAfter;
        AppliedAtUtc = appliedAtUtc;
    }
}
