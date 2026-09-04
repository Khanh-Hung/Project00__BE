using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.ActionExecution;
using Application.Contracts.CognitiveCycle;
using Application.Interfaces;
using Domain.Enums;
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
        var cognitiveEvent = context.Event;

        if (cycleId == Guid.Empty)
        {
            return CharacterCognitiveCycleResult.InvalidInput(
                cycleId, executionId, characterId, triggeredAtUtc, "CycleId cannot be empty.", cognitiveEvent);
        }

        if (executionId == Guid.Empty)
        {
            return CharacterCognitiveCycleResult.InvalidInput(
                cycleId, executionId, characterId, triggeredAtUtc, "ExecutionId cannot be empty.", cognitiveEvent);
        }

        if (characterId == Guid.Empty)
        {
            return CharacterCognitiveCycleResult.InvalidInput(
                cycleId, executionId, characterId, triggeredAtUtc, "CharacterId cannot be empty.", cognitiveEvent);
        }

        if (triggeredAtUtc == default)
        {
            return CharacterCognitiveCycleResult.InvalidInput(
                cycleId, executionId, characterId, triggeredAtUtc, "TriggeredAtUtc must be an explicit, valid timestamp.", cognitiveEvent);
        }

        // Event Consistency & Invariant Validation
        if (cognitiveEvent != null)
        {
            if (cognitiveEvent.CharacterId != characterId)
            {
                return CharacterCognitiveCycleResult.InvalidInput(
                    cycleId, executionId, characterId, triggeredAtUtc,
                    $"Event CharacterId '{cognitiveEvent.CharacterId}' does not match context CharacterId '{characterId}'.",
                    cognitiveEvent);
            }

            if (cognitiveEvent.EventId == Guid.Empty)
            {
                return CharacterCognitiveCycleResult.InvalidInput(
                    cycleId, executionId, characterId, triggeredAtUtc,
                    "Event EventId cannot be empty.",
                    cognitiveEvent);
            }

            if (cognitiveEvent.OccurredAtUtc == default)
            {
                return CharacterCognitiveCycleResult.InvalidInput(
                    cycleId, executionId, characterId, triggeredAtUtc,
                    "Event OccurredAtUtc must be an explicit, valid timestamp.",
                    cognitiveEvent);
            }
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
                $"Authoritative character state for CharacterId {characterId} not found.",
                cognitiveEvent);
        }

        int stateVersionAtStart = state.Version;

        // 2. Perception & Internal Experience (PR39/PR46: Map event to normalized Domain stimulus)
        var stimulus = MapToPerceptionStimulus(cognitiveEvent);
        var perceptionContext = context.PerceptionContext != null
            ? (context.PerceptionContext.Stimulus == null && stimulus != null
                ? context.PerceptionContext with { Stimulus = stimulus }
                : context.PerceptionContext)
            : new CharacterPerceptionContext(
                EvaluatedAtUtc: triggeredAtUtc.UtcDateTime,
                CharacterId: characterId,
                Stimulus: stimulus
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
                @event: cognitiveEvent,
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
                @event: cognitiveEvent,
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
                experience, appraisal, emotion, desires, intent, actionProposal, executionResult,
                @event: cognitiveEvent),

            CharacterActionExecutionStatus.AlreadyExecuted => CharacterCognitiveCycleResult.AlreadyExecuted(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience, appraisal, emotion, desires, intent, actionProposal, executionResult,
                @event: cognitiveEvent),

            CharacterActionExecutionStatus.ConcurrencyConflict => CharacterCognitiveCycleResult.ConcurrencyConflict(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience, appraisal, emotion, desires, intent, actionProposal, executionResult,
                @event: cognitiveEvent,
                message: executionResult.Message),

            CharacterActionExecutionStatus.IdempotencyConflict => CharacterCognitiveCycleResult.IdempotencyConflict(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience, appraisal, emotion, desires, intent, actionProposal, executionResult,
                @event: cognitiveEvent,
                message: executionResult.Message),

            CharacterActionExecutionStatus.NotFound => CharacterCognitiveCycleResult.NotFound(
                cycleId, executionId, characterId, triggeredAtUtc,
                executionResult.Message ?? $"Character {characterId} not found during action execution.",
                cognitiveEvent),

            _ => CharacterCognitiveCycleResult.Failed(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                actionExecution: executionResult,
                @event: cognitiveEvent,
                message: executionResult.Message ?? "Action execution failed.")
        };
    }

    private static CharacterPerceptionStimulus? MapToPerceptionStimulus(CharacterCognitiveEvent? cognitiveEvent)
    {
        if (cognitiveEvent == null)
        {
            return null;
        }

        return cognitiveEvent switch
        {
            UserMessageCognitiveEvent userMsg => new CharacterPerceptionStimulus(
                type: PerceptionStimulusType.UserMessage,
                source: userMsg.Source,
                content: userMsg.Message,
                occurredAtUtc: userMsg.OccurredAtUtc
            ),
            WorldCognitiveEvent worldEvt => new CharacterPerceptionStimulus(
                type: PerceptionStimulusType.WorldEvent,
                source: worldEvt.Source,
                content: worldEvt.EventName,
                occurredAtUtc: worldEvt.OccurredAtUtc,
                category: worldEvt.Category
            ),
            _ => throw new NotSupportedException($"Unsupported cognitive event type: {cognitiveEvent.GetType().Name}")
        };
    }
}
