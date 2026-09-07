using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.CognitiveCycle;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.CognitiveCycle;

/// <summary>
/// Infrastructure service coordinating relationship feedback evaluation and transition persistence.
/// Invariant: Must NEVER mutate CharacterState metrics.
/// Invariant: Non-fatal persistence failure must not roll back committed CharacterState or action execution.
/// </summary>
public sealed class CharacterRelationshipFeedbackService : ICharacterRelationshipFeedbackService
{
    private readonly ICharacterRelationshipTransitionService _transitionService;
    private readonly ICharacterRelationshipFeedbackPolicy _feedbackPolicy;
    private readonly ILogger<CharacterRelationshipFeedbackService> _logger;

    public CharacterRelationshipFeedbackService(
        ICharacterRelationshipTransitionService transitionService,
        ICharacterRelationshipFeedbackPolicy feedbackPolicy,
        ILogger<CharacterRelationshipFeedbackService> logger)
    {
        _transitionService = transitionService ?? throw new ArgumentNullException(nameof(transitionService));
        _feedbackPolicy = feedbackPolicy ?? throw new ArgumentNullException(nameof(feedbackPolicy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterRelationshipFeedback?> RecordFeedbackAsync(
        CharacterCognitiveCycleContext cycleContext,
        CharacterCognitiveCycleResult cycleResult,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cycleContext);
        ArgumentNullException.ThrowIfNull(cycleResult);

        if (cycleResult.Status is CharacterCognitiveCycleStatus.InvalidInput or CharacterCognitiveCycleStatus.NotFound)
        {
            return null;
        }

        var targetInfo = cycleContext.Event?.Target;
        if (!targetInfo.HasValue)
        {
            return null;
        }

        var (targetType, targetId) = targetInfo.Value;

        var proposal = _feedbackPolicy.Evaluate(cycleContext, cycleResult);
        if (proposal == null)
        {
            return null;
        }

        try
        {
            return await _transitionService.ApplyTransitionAsync(
                characterId: cycleContext.CharacterId,
                executionId: cycleContext.ExecutionId,
                targetId: targetId,
                targetType: targetType,
                trustDelta: proposal.TrustDelta,
                affectionDelta: proposal.AffectionDelta,
                familiarityDelta: proposal.FamiliarityDelta,
                newRelationshipType: proposal.NewRelationshipType,
                reason: proposal.Reason,
                occurredAtUtc: cycleContext.TriggeredAtUtc,
                ct: ct);
        }
        catch (CharacterRelationshipIdempotencyConflictException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[CharacterRelationshipFeedbackService] Non-fatal error persisting relationship feedback for CharacterId={CharacterId}, TargetId={TargetId}, ExecutionId={ExecutionId}. State transition remains committed.",
                cycleContext.CharacterId, targetId, cycleContext.ExecutionId);

            return null;
        }
    }
}
