using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Common;
using Application.Contracts.ActionExecution;
using Application.Enums;
using Application.Interfaces;
using Domain.Policies;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.ActionExecution;

public sealed class CharacterActionExecutionService : ICharacterActionExecutionService
{
    private readonly ICharacterStateTransitionService _stateTransitionService;
    private readonly ICharacterActionExecutionPolicy _executionPolicy;
    private readonly ILogger<CharacterActionExecutionService> _logger;

    public CharacterActionExecutionService(
        ICharacterStateTransitionService stateTransitionService,
        ICharacterActionExecutionPolicy executionPolicy,
        ILogger<CharacterActionExecutionService> logger)
    {
        _stateTransitionService = stateTransitionService ?? throw new ArgumentNullException(nameof(stateTransitionService));
        _executionPolicy = executionPolicy ?? throw new ArgumentNullException(nameof(executionPolicy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterActionExecutionResult> ExecuteAsync(
        Guid characterId,
        CharacterActionProposal proposal,
        CharacterActionExecutionContext context,
        CancellationToken ct = default)
    {
        var executionId = context?.ExecutionId ?? Guid.Empty;

        if (characterId == Guid.Empty)
        {
            return CharacterActionExecutionResult.InvalidProposal(
                executionId, characterId, proposal, "CharacterId cannot be empty.");
        }

        if (context == null || context.ExecutionId == Guid.Empty)
        {
            return CharacterActionExecutionResult.InvalidProposal(
                executionId, characterId, proposal, "ExecutionId cannot be empty.");
        }

        if (proposal == null)
        {
            return CharacterActionExecutionResult.InvalidProposal(
                context.ExecutionId, characterId, null, "CharacterActionProposal cannot be null.");
        }

        CharacterStateDelta delta;
        try
        {
            delta = _executionPolicy.CalculateDelta(proposal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to calculate delta for proposal {ProposalType} on Character {CharacterId}", proposal.Type, characterId);
            return CharacterActionExecutionResult.InvalidProposal(
                context.ExecutionId, characterId, proposal, ex.Message);
        }

        var transitionContext = new StateTransitionContext(
            ExecutionId: context.ExecutionId,
            SourceType: "CharacterActionProposal",
            SourceId: $"{proposal.Type}:{proposal.SourceIntent}:{proposal.Motivation}:{proposal.StateVersion}",
            Reason: $"Action proposal {proposal.Type} executed (Intent: {proposal.SourceIntent}, Motivation: {proposal.Motivation})"
        );

        var nowUtc = context.ExecutedAtUtc?.UtcDateTime ?? DateTime.UtcNow;

        var transitionResult = await _stateTransitionService.TransitionAsync(
            characterId,
            delta,
            transitionContext,
            nowUtc,
            ct);

        return transitionResult.Status switch
        {
            StateTransitionResultStatus.Applied => CharacterActionExecutionResult.Applied(
                context.ExecutionId,
                characterId,
                proposal,
                transitionResult.VersionBefore,
                transitionResult.VersionAfter,
                delta,
                transitionResult.Snapshot!),

            StateTransitionResultStatus.AlreadyApplied => CharacterActionExecutionResult.AlreadyExecuted(
                context.ExecutionId,
                characterId,
                proposal,
                transitionResult.VersionAfter,
                delta,
                transitionResult.Snapshot!),

            StateTransitionResultStatus.IdempotencyConflict => CharacterActionExecutionResult.IdempotencyConflict(
                context.ExecutionId,
                characterId,
                proposal,
                transitionResult.Message ?? "ExecutionId already committed with different payload."),

            StateTransitionResultStatus.ConcurrencyConflict => CharacterActionExecutionResult.ConcurrencyConflict(
                context.ExecutionId,
                characterId,
                proposal,
                transitionResult.VersionBefore,
                transitionResult.Message),

            StateTransitionResultStatus.NotFound => CharacterActionExecutionResult.NotFound(
                context.ExecutionId,
                characterId,
                proposal,
                transitionResult.Message ?? $"Authoritative character state for CharacterId {characterId} not found."),

            _ => CharacterActionExecutionResult.InvalidProposal(
                context.ExecutionId,
                characterId,
                proposal,
                transitionResult.Message ?? "State transition rejected.")
        };
    }
}
