using System;
using System.Security.Cryptography;
using System.Text;
using Domain.Common;

namespace Domain.Entities;

/// <summary>
/// Durable audit and idempotency record for character goal progress application per cognitive cycle execution.
/// Enforces unique boundary on (GoalId, ExecutionId) to guarantee at-most-once execution progress.
/// </summary>
public sealed class CharacterGoalExecutionProgress : BaseEntity
{
    public Guid CharacterId { get; private set; }
    public Guid GoalId { get; private set; }
    public Guid ExecutionId { get; private set; }
    public string ActionType { get; private set; }
    public int ProgressDelta { get; private set; }
    public int OldProgress { get; private set; }
    public int NewProgress { get; private set; }
    public string OperationFingerprint { get; private set; }
    public DateTime AppliedAtUtc { get; private set; }

    private CharacterGoalExecutionProgress() : base() 
    {
        ActionType = null!;
        OperationFingerprint = null!;
    }

    public CharacterGoalExecutionProgress(
        Guid characterId,
        Guid goalId,
        Guid executionId,
        string actionType,
        int progressDelta,
        int oldProgress,
        int newProgress,
        DateTimeOffset appliedAtUtc,
        Guid? id = null) : base(id ?? Guid.CreateVersion7())
    {
        if (characterId == Guid.Empty) throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));
        if (goalId == Guid.Empty) throw new ArgumentException("GoalId cannot be empty.", nameof(goalId));
        if (executionId == Guid.Empty) throw new ArgumentException("ExecutionId cannot be empty.", nameof(executionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(actionType, nameof(actionType));

        CharacterId = characterId;
        GoalId = goalId;
        ExecutionId = executionId;
        ActionType = actionType.Trim();
        ProgressDelta = progressDelta;
        OldProgress = oldProgress;
        NewProgress = newProgress;
        AppliedAtUtc = appliedAtUtc.UtcDateTime;
        OperationFingerprint = ComputeFingerprint(characterId, goalId, executionId, ActionType, progressDelta);
    }

    public static string ComputeFingerprint(
        Guid characterId,
        Guid goalId,
        Guid executionId,
        string actionType,
        int progressDelta)
    {
        var raw = $"{characterId:D}:{goalId:D}:{executionId:D}:{actionType.Trim().ToUpperInvariant()}:{progressDelta}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
