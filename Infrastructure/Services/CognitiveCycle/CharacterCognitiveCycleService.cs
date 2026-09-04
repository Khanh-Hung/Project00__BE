using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.ActionExecution;
using Application.Contracts.CognitiveCycle;
using Application.Interfaces;
using Domain.Policies;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.CognitiveCycle;

public sealed class CharacterCognitiveCycleService : ICharacterCognitiveCycleService
{
    private readonly ICharacterStateService _stateService;
    private readonly ICharacterInternalExperiencePolicy _experiencePolicy;
    private readonly ICharacterAppraisalPolicy _appraisalPolicy;
    private readonly ICharacterEmotionPolicy _emotionPolicy;
    private readonly ICharacterDesirePolicy _desirePolicy;
    private readonly ICharacterIntentPolicy _intentPolicy;
    private readonly ICharacterActionProposalPolicy _actionProposalPolicy;
    private readonly ICharacterActionExecutionService _actionExecutionService;
    private readonly ILogger<CharacterCognitiveCycleService> _logger;

    public CharacterCognitiveCycleService(
        ICharacterStateService stateService,
        ICharacterInternalExperiencePolicy experiencePolicy,
        ICharacterAppraisalPolicy appraisalPolicy,
        ICharacterEmotionPolicy emotionPolicy,
        ICharacterDesirePolicy desirePolicy,
        ICharacterIntentPolicy intentPolicy,
        ICharacterActionProposalPolicy actionProposalPolicy,
        ICharacterActionExecutionService actionExecutionService,
        ILogger<CharacterCognitiveCycleService> logger)
    {
        _stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
        _experiencePolicy = experiencePolicy ?? throw new ArgumentNullException(nameof(experiencePolicy));
        _appraisalPolicy = appraisalPolicy ?? throw new ArgumentNullException(nameof(appraisalPolicy));
        _emotionPolicy = emotionPolicy ?? throw new ArgumentNullException(nameof(emotionPolicy));
        _desirePolicy = desirePolicy ?? throw new ArgumentNullException(nameof(desirePolicy));
        _intentPolicy = intentPolicy ?? throw new ArgumentNullException(nameof(intentPolicy));
        _actionProposalPolicy = actionProposalPolicy ?? throw new ArgumentNullException(nameof(actionProposalPolicy));
        _actionExecutionService = actionExecutionService ?? throw new ArgumentNullException(nameof(actionExecutionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CharacterCognitiveCycleResult> RunAsync(
        CharacterCognitiveCycleContext context,
        CancellationToken cancellationToken = default)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var cycleId = context.CycleId;
        var executionId = context.ExecutionId;
        var characterId = context.CharacterId;
        var triggeredAtUtc = context.TriggeredAtUtc;

        if (cycleId == Guid.Empty)
        {
            return CharacterCognitiveCycleResult.InvalidInput(
                cycleId, executionId, characterId, triggeredAtUtc, "CycleId cannot be empty.");
        }

        if (executionId == Guid.Empty)
        {
            return CharacterCognitiveCycleResult.InvalidInput(
                cycleId, executionId, characterId, triggeredAtUtc, "ExecutionId cannot be empty.");
        }

        if (characterId == Guid.Empty)
        {
            return CharacterCognitiveCycleResult.InvalidInput(
                cycleId, executionId, characterId, triggeredAtUtc, "CharacterId cannot be empty.");
        }

        if (triggeredAtUtc == default)
        {
            return CharacterCognitiveCycleResult.InvalidInput(
                cycleId, executionId, characterId, triggeredAtUtc, "TriggeredAtUtc must be an explicit, valid timestamp.");
        }

        // 1. Authoritative State Loading (Strict: always load from authoritative state service, zero caller injection)
        var state = await _stateService.GetAsync(characterId, cancellationToken);
        if (state == null)
        {
            _logger.LogWarning(
                "[CharacterCognitiveCycleService] Authoritative CharacterState for CharacterId={CharacterId} not found. Refusing to fail-open.",
                characterId);

            return CharacterCognitiveCycleResult.NotFound(
                cycleId, executionId, characterId, triggeredAtUtc,
                $"Authoritative character state for CharacterId {characterId} not found.");
        }

        int stateVersionAtStart = state.Version;

        // 2. Perception & Internal Experience (PR39)
        var perceptionContext = context.PerceptionContext ?? new CharacterPerceptionContext(
            EvaluatedAtUtc: triggeredAtUtc.UtcDateTime,
            CharacterId: characterId
        );

        var experience = _experiencePolicy.Evaluate(state, perceptionContext, context.Blueprint?.Psychology);

        // 3. Appraisal (PR40)
        var appraisal = _appraisalPolicy.Evaluate(experience, context.Blueprint);

        // 4. Emotion (PR40)
        var emotion = _emotionPolicy.Evaluate(appraisal, context.Blueprint);

        // 5. Desire (PR41)
        var desires = _desirePolicy.Evaluate(experience, appraisal, emotion);

        // 6. Intent (PR42)
        var intentContext = new CharacterIntentContext(triggeredAtUtc);
        var intent = _intentPolicy.Evaluate(desireEvaluation: desires, context: intentContext);

        // Early Exit: No Intent formed
        if (intent.Intent == null)
        {
            _logger.LogInformation(
                "[CharacterCognitiveCycleService] No actionable intent formed for CharacterId={CharacterId}, CycleId={CycleId}. Cycle stopping without action.",
                characterId, cycleId);

            return CharacterCognitiveCycleResult.CompletedWithoutAction(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience: experience, appraisal: appraisal, emotion: emotion, desires: desires, intent: intent,
                message: "No actionable intent formed from desires.");
        }

        // 7. Action Proposal (PR43)
        var proposalContext = new CharacterActionProposalContext(triggeredAtUtc);
        var actionProposal = _actionProposalPolicy.Evaluate(intent, proposalContext);

        // Early Exit: No Proposal formed
        if (actionProposal.Proposal == null)
        {
            _logger.LogInformation(
                "[CharacterCognitiveCycleService] No actionable proposal formed for CharacterId={CharacterId}, CycleId={CycleId}. Cycle stopping without action.",
                characterId, cycleId);

            return CharacterCognitiveCycleResult.CompletedWithoutAction(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience: experience, appraisal: appraisal, emotion: emotion, desires: desires, intent: intent,
                actionProposal: actionProposal,
                message: "No actionable proposal formed from intent.");
        }

        // 8. Action Execution (PR44)
        var executionContext = new CharacterActionExecutionContext(
            ExecutionId: executionId,
            ExecutedAtUtc: triggeredAtUtc
        );

        var executionResult = await _actionExecutionService.ExecuteAsync(
            characterId: characterId,
            proposal: actionProposal.Proposal,
            context: executionContext,
            ct: cancellationToken
        );

        // 9. Map ActionExecutionResult to CognitiveCycleResult
        return executionResult.Status switch
        {
            CharacterActionExecutionStatus.Applied => CharacterCognitiveCycleResult.CompletedWithAction(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience, appraisal, emotion, desires, intent, actionProposal, executionResult),

            CharacterActionExecutionStatus.AlreadyExecuted => CharacterCognitiveCycleResult.AlreadyExecuted(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience, appraisal, emotion, desires, intent, actionProposal, executionResult),

            CharacterActionExecutionStatus.ConcurrencyConflict => CharacterCognitiveCycleResult.ConcurrencyConflict(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience, appraisal, emotion, desires, intent, actionProposal, executionResult,
                executionResult.Message),

            CharacterActionExecutionStatus.IdempotencyConflict => CharacterCognitiveCycleResult.IdempotencyConflict(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience, appraisal, emotion, desires, intent, actionProposal, executionResult,
                executionResult.Message),

            CharacterActionExecutionStatus.NotFound => CharacterCognitiveCycleResult.NotFound(
                cycleId, executionId, characterId, triggeredAtUtc,
                executionResult.Message ?? $"Character {characterId} not found during action execution."),

            _ => CharacterCognitiveCycleResult.Failed(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                actionExecution: executionResult,
                message: executionResult.Message ?? "Action execution failed.")
        };
    }
}
