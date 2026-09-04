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
    private readonly ICharacterMemoryRetrievalService? _memoryRetrievalService;
    private readonly ICharacterMemoryFeedbackService? _memoryFeedbackService;
    private readonly ICharacterRelationshipRetrievalService? _relationshipRetrievalService;
    private readonly ICharacterRelationshipFeedbackService? _relationshipFeedbackService;
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
        ILogger<CharacterCognitiveCycleService> logger,
        ICharacterMemoryRetrievalService? memoryRetrievalService = null,
        ICharacterMemoryFeedbackService? memoryFeedbackService = null,
        ICharacterRelationshipRetrievalService? relationshipRetrievalService = null,
        ICharacterRelationshipFeedbackService? relationshipFeedbackService = null)
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
        _memoryRetrievalService = memoryRetrievalService;
        _memoryFeedbackService = memoryFeedbackService;
        _relationshipRetrievalService = relationshipRetrievalService;
        _relationshipFeedbackService = relationshipFeedbackService;
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
        CharacterPerceptionStimulus? stimulus = null;
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

            if (string.IsNullOrWhiteSpace(cognitiveEvent.Source))
            {
                return CharacterCognitiveCycleResult.InvalidInput(
                    cycleId, executionId, characterId, triggeredAtUtc,
                    "Event Source cannot be empty.",
                    cognitiveEvent);
            }

            switch (cognitiveEvent)
            {
                case UserMessageCognitiveEvent userMsg when string.IsNullOrWhiteSpace(userMsg.Message):
                    return CharacterCognitiveCycleResult.InvalidInput(
                        cycleId, executionId, characterId, triggeredAtUtc,
                        "UserMessage message cannot be empty.",
                        cognitiveEvent);

                case WorldCognitiveEvent worldEvt when string.IsNullOrWhiteSpace(worldEvt.EventName):
                    return CharacterCognitiveCycleResult.InvalidInput(
                        cycleId, executionId, characterId, triggeredAtUtc,
                        "WorldEvent eventName cannot be empty.",
                        cognitiveEvent);
            }

            stimulus = MapToPerceptionStimulus(cognitiveEvent);

            if (context.PerceptionContext?.Stimulus != null && !context.PerceptionContext.Stimulus.Equals(stimulus))
            {
                return CharacterCognitiveCycleResult.InvalidInput(
                    cycleId, executionId, characterId, triggeredAtUtc,
                    "Conflicting stimulus detected: When an Event is provided, it is the sole source of external stimulus for the cycle. PerceptionContext.Stimulus must either be null or match the mapped Event.",
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

        // Validate caller did not attempt to inject MemoryContext via PerceptionContext
        if (context.PerceptionContext?.MemoryContext != null)
        {
            _logger.LogWarning(
                "[CharacterCognitiveCycleService] Caller attempted to inject MemoryContext via PerceptionContext for CharacterId={CharacterId}, CycleId={CycleId}. Rejecting invalid input.",
                characterId, cycleId);

            return CharacterCognitiveCycleResult.InvalidInput(
                cycleId, executionId, characterId, triggeredAtUtc,
                message: "PerceptionContext.MemoryContext cannot be pre-populated by caller. Memory retrieval is managed authoritatively by the cognitive cycle.",
                @event: cognitiveEvent);
        }

        // Validate caller did not attempt to inject RelationshipContext via PerceptionContext
        if (context.PerceptionContext?.RelationshipContext != null)
        {
            _logger.LogWarning(
                "[CharacterCognitiveCycleService] Caller attempted to inject RelationshipContext via PerceptionContext for CharacterId={CharacterId}, CycleId={CycleId}. Rejecting invalid input.",
                characterId, cycleId);

            return CharacterCognitiveCycleResult.InvalidInput(
                cycleId, executionId, characterId, triggeredAtUtc,
                message: "PerceptionContext.RelationshipContext cannot be pre-populated by caller. Relationship retrieval is managed authoritatively by the cognitive cycle.",
                @event: cognitiveEvent);
        }

        // 2. Perception & Stimulus Mapping (PR39/PR46: Map event to normalized Domain stimulus)
        var basePerceptionContext = context.PerceptionContext != null
            ? (stimulus != null ? context.PerceptionContext with { Stimulus = stimulus } : context.PerceptionContext)
            : new CharacterPerceptionContext(
                EvaluatedAtUtc: triggeredAtUtc.UtcDateTime,
                CharacterId: characterId,
                Stimulus: stimulus
            );

        // 2.5 Relationship Retrieval (PR48: Authoritative contextual social state, graceful degradation)
        CharacterRelationshipContext? relationshipContext = null;
        if (_relationshipRetrievalService != null)
        {
            try
            {
                relationshipContext = await _relationshipRetrievalService.RetrieveRelationshipAsync(characterId, cognitiveEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[CharacterCognitiveCycleService] Failed to retrieve relationship for CharacterId={CharacterId}. Gracefully falling back to null.",
                    characterId);
                relationshipContext = null;
            }
        }

        // Validate caller-provided RelationshipContext: caller data must not conflict with authoritative relationship state
        if (context.RelationshipContext != null && !context.RelationshipContext.Equals(relationshipContext))
        {
            _logger.LogWarning(
                "[CharacterCognitiveCycleService] Caller provided conflicting RelationshipContext for CharacterId={CharacterId}, CycleId={CycleId}. Rejecting invalid input.",
                characterId, cycleId);

            return CharacterCognitiveCycleResult.InvalidInput(
                cycleId, executionId, characterId, triggeredAtUtc,
                message: "Conflicting RelationshipContext detected: Caller cannot override authoritative relationship state.",
                @event: cognitiveEvent,
                relationshipContext: relationshipContext);
        }

        // 3. Memory Retrieval (PR47: Contextual knowledge, graceful degradation to empty)
        // Authoritative memory retrieval boundary: caller cannot bypass or inject arbitrary memories.
        CharacterMemoryContext memoryContext;
        if (_memoryRetrievalService != null)
        {
            try
            {
                memoryContext = await _memoryRetrievalService.RetrieveRelevantAsync(characterId, basePerceptionContext, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[CharacterCognitiveCycleService] Failed to retrieve memories for CharacterId={CharacterId}. Gracefully falling back to empty memory context.",
                    characterId);
                memoryContext = CharacterMemoryContext.Empty;
            }
        }
        else
        {
            memoryContext = CharacterMemoryContext.Empty;
        }

        var perceptionContext = basePerceptionContext with
        {
            MemoryContext = memoryContext,
            RelationshipContext = relationshipContext
        };

        // 4. Internal Experience (PR39)
        var experience = _experiencePolicy.Evaluate(state, perceptionContext, context.Blueprint?.Psychology);

        // 5. Appraisal (PR40)
        var appraisal = _appraisalPolicy.Evaluate(experience, context.Blueprint);

        // 6. Emotion (PR40)
        var emotion = _emotionPolicy.Evaluate(appraisal, context.Blueprint);

        // 7. Desire (PR41)
        var desires = _desirePolicy.Evaluate(experience, appraisal, emotion);

        // 8. Intent (PR42)
        var intentContext = new CharacterIntentContext(triggeredAtUtc);
        var intent = _intentPolicy.Evaluate(desireEvaluation: desires, context: intentContext);

        // Early Exit: No Intent formed
        if (intent.Intent == null)
        {
            _logger.LogInformation(
                "[CharacterCognitiveCycleService] No actionable intent formed for CharacterId={CharacterId}, CycleId={CycleId}. Cycle stopping without action.",
                characterId, cycleId);

            var noIntentResult = CharacterCognitiveCycleResult.CompletedWithoutAction(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience: experience, appraisal: appraisal, emotion: emotion, desires: desires, intent: intent,
                @event: cognitiveEvent,
                memoryContext: memoryContext,
                relationshipContext: relationshipContext,
                message: "No actionable intent formed from desires.");

            var withMemory = await AttachMemoryFeedbackAsync(context, noIntentResult, cancellationToken);
            return await AttachRelationshipFeedbackAsync(context, withMemory, cancellationToken);
        }

        // 9. Action Proposal (PR43)
        var proposalContext = new CharacterActionProposalContext(triggeredAtUtc);
        var actionProposal = _actionProposalPolicy.Evaluate(intent, proposalContext);

        // Early Exit: No Proposal formed
        if (actionProposal.Proposal == null)
        {
            _logger.LogInformation(
                "[CharacterCognitiveCycleService] No actionable proposal formed for CharacterId={CharacterId}, CycleId={CycleId}. Cycle stopping without action.",
                characterId, cycleId);

            var noProposalResult = CharacterCognitiveCycleResult.CompletedWithoutAction(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience: experience, appraisal: appraisal, emotion: emotion, desires: desires, intent: intent,
                actionProposal: actionProposal,
                @event: cognitiveEvent,
                memoryContext: memoryContext,
                relationshipContext: relationshipContext,
                message: "No actionable proposal formed from intent.");

            var withMemory = await AttachMemoryFeedbackAsync(context, noProposalResult, cancellationToken);
            return await AttachRelationshipFeedbackAsync(context, withMemory, cancellationToken);
        }

        // 10. Action Execution (PR44)
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

        // 11. Map ActionExecutionResult to CognitiveCycleResult
        var cycleResult = executionResult.Status switch
        {
            CharacterActionExecutionStatus.Applied => CharacterCognitiveCycleResult.CompletedWithAction(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience, appraisal, emotion, desires, intent, actionProposal, executionResult,
                @event: cognitiveEvent,
                memoryContext: memoryContext,
                relationshipContext: relationshipContext),

            CharacterActionExecutionStatus.AlreadyExecuted => CharacterCognitiveCycleResult.AlreadyExecuted(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience, appraisal, emotion, desires, intent, actionProposal, executionResult,
                @event: cognitiveEvent,
                memoryContext: memoryContext,
                relationshipContext: relationshipContext),

            CharacterActionExecutionStatus.ConcurrencyConflict => CharacterCognitiveCycleResult.ConcurrencyConflict(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience, appraisal, emotion, desires, intent, actionProposal, executionResult,
                @event: cognitiveEvent,
                memoryContext: memoryContext,
                relationshipContext: relationshipContext,
                message: executionResult.Message),

            CharacterActionExecutionStatus.IdempotencyConflict => CharacterCognitiveCycleResult.IdempotencyConflict(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience, appraisal, emotion, desires, intent, actionProposal, executionResult,
                @event: cognitiveEvent,
                memoryContext: memoryContext,
                relationshipContext: relationshipContext,
                message: executionResult.Message),

            CharacterActionExecutionStatus.NotFound => CharacterCognitiveCycleResult.NotFound(
                cycleId, executionId, characterId, triggeredAtUtc,
                executionResult.Message ?? $"Character {characterId} not found during action execution.",
                cognitiveEvent,
                memoryContext: memoryContext,
                relationshipContext: relationshipContext),

            _ => CharacterCognitiveCycleResult.Failed(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                actionExecution: executionResult,
                @event: cognitiveEvent,
                memoryContext: memoryContext,
                relationshipContext: relationshipContext,
                message: executionResult.Message ?? "Action execution failed.")
        };

        // 12. Persist Memory Feedback (PR47: Independent identity, error does not roll back state)
        var resultWithMemory = await AttachMemoryFeedbackAsync(context, cycleResult, cancellationToken);

        // 13. Persist Relationship Feedback (PR48: Independent identity, error does not roll back state)
        return await AttachRelationshipFeedbackAsync(context, resultWithMemory, cancellationToken);
    }

    private async Task<CharacterCognitiveCycleResult> AttachMemoryFeedbackAsync(
        CharacterCognitiveCycleContext context,
        CharacterCognitiveCycleResult result,
        CancellationToken ct)
    {
        if (_memoryFeedbackService == null)
        {
            return result;
        }

        try
        {
            var feedback = await _memoryFeedbackService.RecordFeedbackAsync(context, result, ct);
            if (feedback != null)
            {
                return result with { MemoryFeedback = feedback };
            }
        }
        catch (CharacterMemoryIdempotencyConflictException ex)
        {
            _logger.LogWarning(ex,
                "[CharacterCognitiveCycleService] Idempotency conflict detected in memory feedback for CharacterId={CharacterId}, ExecutionId={ExecutionId}. {Message}",
                context.CharacterId, context.ExecutionId, ex.Message);

            return CharacterCognitiveCycleResult.IdempotencyConflict(
                context.CycleId,
                context.ExecutionId,
                context.CharacterId,
                context.TriggeredAtUtc,
                result.StateVersionAtStart,
                result.Experience,
                result.Appraisal,
                result.Emotion,
                result.Desires,
                result.Intent,
                result.ActionProposal,
                result.ActionExecution,
                result.Event,
                result.MemoryContext,
                memoryFeedback: null,
                relationshipContext: result.RelationshipContext,
                relationshipFeedback: result.RelationshipFeedback,
                message: ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[CharacterCognitiveCycleService] Failed to record memory feedback for CharacterId={CharacterId}, CycleId={CycleId}. State transition remains committed.",
                context.CharacterId, context.CycleId);
        }

        return result;
    }

    private async Task<CharacterCognitiveCycleResult> AttachRelationshipFeedbackAsync(
        CharacterCognitiveCycleContext context,
        CharacterCognitiveCycleResult result,
        CancellationToken ct)
    {
        if (_relationshipFeedbackService == null)
        {
            return result;
        }

        try
        {
            var feedback = await _relationshipFeedbackService.RecordFeedbackAsync(context, result, ct);
            if (feedback != null)
            {
                return result with { RelationshipFeedback = feedback };
            }
        }
        catch (CharacterRelationshipIdempotencyConflictException ex)
        {
            _logger.LogWarning(ex,
                "[CharacterCognitiveCycleService] Idempotency conflict detected in relationship feedback for CharacterId={CharacterId}, ExecutionId={ExecutionId}. {Message}",
                context.CharacterId, context.ExecutionId, ex.Message);

            return CharacterCognitiveCycleResult.IdempotencyConflict(
                context.CycleId,
                context.ExecutionId,
                context.CharacterId,
                context.TriggeredAtUtc,
                result.StateVersionAtStart,
                result.Experience,
                result.Appraisal,
                result.Emotion,
                result.Desires,
                result.Intent,
                result.ActionProposal,
                result.ActionExecution,
                result.Event,
                result.MemoryContext,
                result.MemoryFeedback,
                relationshipContext: result.RelationshipContext,
                relationshipFeedback: null,
                message: ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[CharacterCognitiveCycleService] Failed to record relationship feedback for CharacterId={CharacterId}, CycleId={CycleId}. State transition remains committed.",
                context.CharacterId, context.CycleId);
        }

        return result;
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
