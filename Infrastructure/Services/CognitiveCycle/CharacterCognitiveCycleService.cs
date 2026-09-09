using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Contracts.ActionExecution;
using Application.Contracts.CognitiveCycle;
using Application.Interfaces;
using Domain.Entities;
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
    private readonly IActionSafetyGate _safetyGate;
    private readonly ICharacterActionExecutionService _actionExecutionService;
    private readonly ICharacterMemoryRetrievalService? _memoryRetrievalService;
    private readonly ICharacterMemoryFeedbackService? _memoryFeedbackService;
    private readonly ICharacterRelationshipRetrievalService? _relationshipRetrievalService;
    private readonly ICharacterRelationshipFeedbackService? _relationshipFeedbackService;
    private readonly IPersonalityAdaptationService? _personalityAdaptationService;
    private readonly ICharacterPersonalityRepository? _personalityRepository;
    private readonly ICharacterGoalService? _goalService;
    private readonly ISocialBehaviorPolicy? _socialBehaviorPolicy;
    private readonly ISocialPresenceTransitionService? _socialPresenceTransitionService;
    private readonly ICharacterSocialPresenceRepository? _socialPresenceRepository;
    private readonly ILogger<CharacterCognitiveCycleService> _logger;

    public CharacterCognitiveCycleService(
        ICharacterStateService stateService,
        ICharacterInternalExperiencePolicy experiencePolicy,
        ICharacterAppraisalPolicy appraisalPolicy,
        ICharacterEmotionPolicy emotionPolicy,
        ICharacterDesirePolicy desirePolicy,
        ICharacterIntentPolicy intentPolicy,
        ICharacterActionProposalPolicy actionProposalPolicy,
        IActionSafetyGate safetyGate,
        ICharacterActionExecutionService actionExecutionService,
        ILogger<CharacterCognitiveCycleService> logger,
        ICharacterMemoryRetrievalService? memoryRetrievalService = null,
        ICharacterMemoryFeedbackService? memoryFeedbackService = null,
        ICharacterRelationshipRetrievalService? relationshipRetrievalService = null,
        ICharacterRelationshipFeedbackService? relationshipFeedbackService = null,
        IPersonalityAdaptationService? personalityAdaptationService = null,
        ICharacterPersonalityRepository? personalityRepository = null,
        ICharacterGoalService? goalService = null,
        ISocialBehaviorPolicy? socialBehaviorPolicy = null,
        ISocialPresenceTransitionService? socialPresenceTransitionService = null,
        ICharacterSocialPresenceRepository? socialPresenceRepository = null)
    {
        _stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
        _experiencePolicy = experiencePolicy ?? throw new ArgumentNullException(nameof(experiencePolicy));
        _appraisalPolicy = appraisalPolicy ?? throw new ArgumentNullException(nameof(appraisalPolicy));
        _emotionPolicy = emotionPolicy ?? throw new ArgumentNullException(nameof(emotionPolicy));
        _desirePolicy = desirePolicy ?? throw new ArgumentNullException(nameof(desirePolicy));
        _intentPolicy = intentPolicy ?? throw new ArgumentNullException(nameof(intentPolicy));
        _actionProposalPolicy = actionProposalPolicy ?? throw new ArgumentNullException(nameof(actionProposalPolicy));
        _safetyGate = safetyGate ?? throw new ArgumentNullException(nameof(safetyGate));
        _actionExecutionService = actionExecutionService ?? throw new ArgumentNullException(nameof(actionExecutionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _memoryRetrievalService = memoryRetrievalService;
        _memoryFeedbackService = memoryFeedbackService;
        _relationshipRetrievalService = relationshipRetrievalService;
        _relationshipFeedbackService = relationshipFeedbackService;
        _personalityAdaptationService = personalityAdaptationService;
        _personalityRepository = personalityRepository;
        _goalService = goalService;
        _socialBehaviorPolicy = socialBehaviorPolicy;
        _socialPresenceTransitionService = socialPresenceTransitionService;
        _socialPresenceRepository = socialPresenceRepository;
    }

    public async Task<CharacterCognitiveCycleResult> RunAsync(
        CharacterCognitiveCycleContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

                case AutonomousCognitiveEvent autoEvt when string.IsNullOrWhiteSpace(autoEvt.EventName):
                    return CharacterCognitiveCycleResult.InvalidInput(
                        cycleId, executionId, characterId, triggeredAtUtc,
                        "AutonomousCognitiveEvent eventName cannot be empty.",
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

        // 1.5 Authoritative Personality Loading & Snapshot Creation (P0-1)
        CharacterPersonality personality;
        if (_personalityRepository != null)
        {
            try
            {
                personality = await _personalityRepository.GetOrCreateDefaultAsync(characterId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[CharacterCognitiveCycleService] Failed to load authoritative personality for CharacterId={CharacterId}. Falling back to default snapshot.",
                    characterId);
                personality = CharacterPersonality.CreateDefault(characterId);
            }
        }
        else
        {
            personality = CharacterPersonality.CreateDefault(characterId);
        }

        var personalitySnapshot = personality.ToSnapshot();
        var effectivePsychology = personalitySnapshot.ToEffectivePsychology(context.Blueprint?.Psychology);
        var effectiveBlueprint = (context.Blueprint ?? new CharacterBlueprint()) with
        {
            Psychology = effectivePsychology
        };

        // Validate caller did not attempt to inject MemoryContext via PerceptionContext
        if (context.PerceptionContext?.MemoryContext != null)
        {
            _logger.LogWarning(
                "[CharacterCognitiveCycleService] Caller attempted to inject MemoryContext via PerceptionContext for CharacterId={CharacterId}, CycleId={CycleId}. Rejecting invalid input.",
                characterId, cycleId);

            return CharacterCognitiveCycleResult.InvalidInput(
                cycleId, executionId, characterId, triggeredAtUtc,
                message: "PerceptionContext.MemoryContext cannot be pre-populated by caller. Memory retrieval is managed authoritatively by the cognitive cycle.",
                @event: cognitiveEvent,
                personalitySnapshot: personalitySnapshot);
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
                @event: cognitiveEvent,
                personalitySnapshot: personalitySnapshot);
        }

        // Validate caller did not attempt to inject GoalContext
        if (context.GoalContext != null)
        {
            _logger.LogWarning(
                "[CharacterCognitiveCycleService] Caller attempted to inject GoalContext for CharacterId={CharacterId}, CycleId={CycleId}. Rejecting invalid input.",
                characterId, cycleId);

            return CharacterCognitiveCycleResult.InvalidInput(
                cycleId, executionId, characterId, triggeredAtUtc,
                message: "GoalContext cannot be pre-populated by caller. Goal evaluation is managed authoritatively by the cognitive cycle.",
                @event: cognitiveEvent,
                personalitySnapshot: personalitySnapshot);
        }

        // Validate caller did not attempt to inject SocialPresenceContext
        if (context.SocialPresenceContext != null)
        {
            _logger.LogWarning(
                "[CharacterCognitiveCycleService] Caller attempted to inject SocialPresenceContext for CharacterId={CharacterId}, CycleId={CycleId}. Rejecting invalid input.",
                characterId, cycleId);

            return CharacterCognitiveCycleResult.InvalidInput(
                cycleId, executionId, characterId, triggeredAtUtc,
                message: "SocialPresenceContext cannot be pre-populated by caller. Social presence is managed authoritatively by the cognitive cycle.",
                @event: cognitiveEvent,
                personalitySnapshot: personalitySnapshot);
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
            catch (OperationCanceledException)
            {
                throw;
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
                relationshipContext: relationshipContext,
                personalitySnapshot: personalitySnapshot);
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
            catch (OperationCanceledException)
            {
                throw;
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

        // 3.5 Authoritative Social Presence Loading (PR57: Contextual social presence state, graceful degradation)
        CharacterSocialPresenceContext? socialPresenceContext = null;
        if (_socialPresenceRepository != null)
        {
            try
            {
                var presence = await _socialPresenceRepository.GetByCharacterIdAsync(characterId, cancellationToken);
                socialPresenceContext = presence?.ToContext();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[CharacterCognitiveCycleService] Failed to load social presence for CharacterId={CharacterId}. Gracefully falling back to null.",
                    characterId);
                socialPresenceContext = null;
            }
        }

        var perceptionContext = basePerceptionContext with
        {
            MemoryContext = memoryContext,
            RelationshipContext = relationshipContext
        };

        // 4. Internal Experience (PR39 modulated by authoritative PersonalitySnapshot)
        var experience = _experiencePolicy.Evaluate(state, perceptionContext, effectivePsychology);

        // 5. Appraisal (PR40 modulated by effective Blueprint)
        var appraisal = _appraisalPolicy.Evaluate(experience, effectiveBlueprint);

        // 6. Emotion (PR40 modulated by effective Blueprint)
        var emotion = _emotionPolicy.Evaluate(appraisal, effectiveBlueprint);

        // 7. Desire (PR41)
        var desires = _desirePolicy.Evaluate(experience, appraisal, emotion);

        // 7.5 Authoritative Goal Evaluation (PR55)
        CharacterGoalContext? goalContext = null;
        if (_goalService != null)
        {
            try
            {
                var activeGoal = await _goalService.GetOrSelectActiveGoalAsync(characterId, desires, triggeredAtUtc, cancellationToken);
                if (activeGoal != null)
                {
                    goalContext = new CharacterGoalContext(
                        GoalId: activeGoal.Id,
                        GoalKey: activeGoal.GoalKey,
                        Status: activeGoal.Status,
                        Priority: (int)activeGoal.Priority,
                        Progress: activeGoal.ProgressPercentage
                    );
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[CharacterCognitiveCycleService] Failed to evaluate goal for CharacterId={CharacterId}. Gracefully falling back to null.",
                    characterId);
                goalContext = null;
            }
        }

        // Early Exit for Autonomous Cycles: Autonomous cycles require an authoritative goal
        if (cognitiveEvent is AutonomousCognitiveEvent && goalContext == null)
        {
            _logger.LogInformation(
                "[CharacterCognitiveCycleService] No active or compatible goal found for autonomous cycle. CharacterId={CharacterId}, CycleId={CycleId}. Cycle stopping without action.",
                characterId, cycleId);

            var noGoalResult = CharacterCognitiveCycleResult.CompletedWithoutAction(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience: experience, appraisal: appraisal, emotion: emotion, desires: desires, intent: null,
                actionProposal: null,
                @event: cognitiveEvent,
                memoryContext: memoryContext,
                relationshipContext: relationshipContext,
                personalitySnapshot: personalitySnapshot,
                message: "No active or compatible goal found for autonomous cycle.");

            var withMemory = await AttachMemoryFeedbackAsync(context, noGoalResult with { SocialPresenceContext = socialPresenceContext }, cancellationToken);
            var withRelationship = await AttachRelationshipFeedbackAsync(context, withMemory, cancellationToken);
            var withPersonality = await AttachPersonalityAdaptationAsync(context, withRelationship, cancellationToken);
            return await AttachSocialPresenceFeedbackAsync(context, withPersonality, cancellationToken);
        }

        // 8. Intent (PR42 modulated by active GoalContext)
        var intentContext = new CharacterIntentContext(triggeredAtUtc, goalContext);
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
                personalitySnapshot: personalitySnapshot,
                message: "No actionable intent formed from desires.");

            var withMemory = await AttachMemoryFeedbackAsync(context, noIntentResult with { GoalContext = goalContext, SocialPresenceContext = socialPresenceContext }, cancellationToken);
            var withRelationship = await AttachRelationshipFeedbackAsync(context, withMemory, cancellationToken);
            var withPersonality = await AttachPersonalityAdaptationAsync(context, withRelationship, cancellationToken);
            return await AttachSocialPresenceFeedbackAsync(context, withPersonality, cancellationToken);
        }

        // 9. Action Proposal (PR43 modulated by active GoalContext and SocialBehaviorDecision)
        SocialBehaviorDecision? socialDecision = null;
        if (_socialBehaviorPolicy != null)
        {
            try
            {
                socialDecision = _socialBehaviorPolicy.Evaluate(
                    characterId,
                    socialPresenceContext,
                    relationshipContext,
                    goalContext,
                    personalitySnapshot);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[CharacterCognitiveCycleService] Failed to evaluate social behavior policy for CharacterId={CharacterId}. Continuing cycle without social modulation.",
                    characterId);
                socialDecision = null;
            }
        }

        var proposalContext = new CharacterActionProposalContext(triggeredAtUtc, goalContext, socialDecision);
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
                personalitySnapshot: personalitySnapshot,
                message: "No actionable proposal formed from intent.");

            var withMemory = await AttachMemoryFeedbackAsync(context, noProposalResult with { GoalContext = goalContext, SocialPresenceContext = socialPresenceContext }, cancellationToken);
            var withRelationship = await AttachRelationshipFeedbackAsync(context, withMemory, cancellationToken);
            var withPersonality = await AttachPersonalityAdaptationAsync(context, withRelationship, cancellationToken);
            return await AttachSocialPresenceFeedbackAsync(context, withPersonality, cancellationToken);
        }

        // 9.5 Safety / Policy Gate Evaluation (PR52: Mandatory execution boundary)
        cancellationToken.ThrowIfCancellationRequested();
        var safetyDecision = await _safetyGate.EvaluateAsync(characterId, actionProposal.Proposal, cancellationToken);
        if (!safetyDecision.IsAllowed)
        {
            _logger.LogWarning(
                "[CharacterCognitiveCycleService] Action proposal {ActionType} for Character {CharacterId} blocked by Safety Gate. Policy: {PolicyCode}, Reason: {Reason}",
                actionProposal.Proposal.Type, characterId, safetyDecision.PolicyCode, safetyDecision.Reason);

            var blockedResult = CharacterCognitiveCycleResult.CompletedWithoutAction(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience: experience, appraisal: appraisal, emotion: emotion, desires: desires, intent: intent,
                actionProposal: actionProposal,
                @event: cognitiveEvent,
                memoryContext: memoryContext,
                relationshipContext: relationshipContext,
                personalitySnapshot: personalitySnapshot,
                message: $"Action proposal blocked by safety policy '{safetyDecision.PolicyCode}': {safetyDecision.Reason}",
                safetyDecision: safetyDecision);

            var withMemory = await AttachMemoryFeedbackAsync(context, blockedResult with { GoalContext = goalContext, SocialPresenceContext = socialPresenceContext }, cancellationToken);
            var withRelationship = await AttachRelationshipFeedbackAsync(context, withMemory, cancellationToken);
            var withPersonality = await AttachPersonalityAdaptationAsync(context, withRelationship, cancellationToken);
            return await AttachSocialPresenceFeedbackAsync(context, withPersonality, cancellationToken);
        }

        // 10. Action Execution (PR44)
        cancellationToken.ThrowIfCancellationRequested();
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
                relationshipContext: relationshipContext,
                personalitySnapshot: personalitySnapshot,
                safetyDecision: safetyDecision),

            CharacterActionExecutionStatus.AlreadyExecuted => CharacterCognitiveCycleResult.AlreadyExecuted(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience, appraisal, emotion, desires, intent, actionProposal, executionResult,
                @event: cognitiveEvent,
                memoryContext: memoryContext,
                relationshipContext: relationshipContext,
                personalitySnapshot: personalitySnapshot),

            CharacterActionExecutionStatus.ConcurrencyConflict => CharacterCognitiveCycleResult.ConcurrencyConflict(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience, appraisal, emotion, desires, intent, actionProposal, executionResult,
                @event: cognitiveEvent,
                memoryContext: memoryContext,
                relationshipContext: relationshipContext,
                personalitySnapshot: personalitySnapshot,
                message: executionResult.Message),

            CharacterActionExecutionStatus.IdempotencyConflict => CharacterCognitiveCycleResult.IdempotencyConflict(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                experience, appraisal, emotion, desires, intent, actionProposal, executionResult,
                @event: cognitiveEvent,
                memoryContext: memoryContext,
                relationshipContext: relationshipContext,
                personalitySnapshot: personalitySnapshot,
                message: executionResult.Message),

            CharacterActionExecutionStatus.NotFound => CharacterCognitiveCycleResult.NotFound(
                cycleId, executionId, characterId, triggeredAtUtc,
                executionResult.Message ?? $"Character {characterId} not found during action execution.",
                cognitiveEvent,
                memoryContext: memoryContext,
                relationshipContext: relationshipContext,
                personalitySnapshot: personalitySnapshot),

            _ => CharacterCognitiveCycleResult.Failed(
                cycleId, executionId, characterId, triggeredAtUtc, stateVersionAtStart,
                actionExecution: executionResult,
                @event: cognitiveEvent,
                memoryContext: memoryContext,
                relationshipContext: relationshipContext,
                personalitySnapshot: personalitySnapshot,
                message: executionResult.Message ?? "Action execution failed.")
        };

        // 12. Persist Memory Feedback (PR47: Independent identity, error does not roll back state)
        var resultWithGoalContext = cycleResult with { GoalContext = goalContext, SocialPresenceContext = socialPresenceContext };
        var resultWithMemory = await AttachMemoryFeedbackAsync(context, resultWithGoalContext, cancellationToken);

        // 13. Persist Relationship Feedback (PR48: Independent identity, error does not roll back state)
        var resultWithRelationship = await AttachRelationshipFeedbackAsync(context, resultWithMemory, cancellationToken);

        // 14. Persist Personality Adaptation (PR49: Independent identity, threshold accumulation, error does not roll back state)
        var resultWithPersonality = await AttachPersonalityAdaptationAsync(context, resultWithRelationship, cancellationToken);

        // 15. Persist Goal Progress Feedback (PR55: Independent identity, error does not roll back state)
        var resultWithGoal = await AttachGoalFeedbackAsync(context, resultWithPersonality, cancellationToken);

        // 16. Persist Social Presence Feedback (PR57: Independent identity, error does not roll back state)
        var finalResult = await AttachSocialPresenceFeedbackAsync(context, resultWithGoal, cancellationToken);

        _logger.LogInformation(
            "Cognitive cycle completed. CharacterId={CharacterId}, CycleId={CycleId}, ExecutionId={ExecutionId}, EventId={EventId}, StateVersionAtStart={StateVersionAtStart}, Status={Status}, ActionType={ActionType}, SafetyPolicy={SafetyPolicy}",
            finalResult.CharacterId,
            finalResult.CycleId,
            finalResult.ExecutionId,
            finalResult.Event?.EventId,
            finalResult.StateVersionAtStart,
            finalResult.Status,
            finalResult.ActionProposal?.Proposal?.Type,
            finalResult.SafetyDecision?.PolicyCode ?? "None");

        return finalResult;
    }

    private async Task<CharacterCognitiveCycleResult> AttachSocialPresenceFeedbackAsync(
        CharacterCognitiveCycleContext context,
        CharacterCognitiveCycleResult result,
        CancellationToken ct)
    {
        if (_socialPresenceTransitionService == null || result.ActionExecution == null)
        {
            return result;
        }

        try
        {
            RelationshipTargetType? targetType = result.SocialPresenceContext?.TargetType;
            Guid? targetId = result.SocialPresenceContext?.TargetId;

            if (result.RelationshipContext != null)
            {
                targetType = result.RelationshipContext.TargetType;
                targetId = result.RelationshipContext.TargetId;
            }
            else if (result.Event?.Target != null)
            {
                targetType = result.Event.Target.Value.TargetType;
                targetId = result.Event.Target.Value.TargetId;
            }

            var feedback = await _socialPresenceTransitionService.ApplyActionExecutionFeedbackAsync(
                characterId: result.CharacterId,
                executionId: result.ExecutionId,
                actionExecution: result.ActionExecution,
                now: result.TriggeredAtUtc,
                targetType: targetType,
                targetId: targetId,
                ct: ct);

            return result with { SocialPresenceFeedback = feedback };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[CharacterCognitiveCycleService] Failed to record social presence feedback for CharacterId={CharacterId}, ExecutionId={ExecutionId}. State transition remains committed.",
                result.CharacterId, result.ExecutionId);
            return result;
        }
    }

    private async Task<CharacterCognitiveCycleResult> AttachGoalFeedbackAsync(
        CharacterCognitiveCycleContext context,
        CharacterCognitiveCycleResult result,
        CancellationToken ct)
    {
        if (_goalService == null || result.GoalContext == null || result.ActionExecution == null)
        {
            return result;
        }

        try
        {
            var feedback = await _goalService.ApplyProgressFeedbackAsync(
                result.CharacterId,
                result.ExecutionId,
                result.ActionExecution,
                result.GoalContext,
                result.TriggeredAtUtc,
                ct);

            return result with { GoalFeedback = feedback };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[CharacterCognitiveCycleService] Failed to record goal progress feedback for CharacterId={CharacterId}, ExecutionId={ExecutionId}. Continuing cycle without failing.",
                result.CharacterId, result.ExecutionId);
            return result;
        }
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
        catch (OperationCanceledException)
        {
            throw;
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
        catch (OperationCanceledException)
        {
            throw;
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
                personalitySnapshot: result.PersonalitySnapshot,
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

    private async Task<CharacterCognitiveCycleResult> AttachPersonalityAdaptationAsync(
        CharacterCognitiveCycleContext context,
        CharacterCognitiveCycleResult result,
        CancellationToken ct)
    {
        if (_personalityAdaptationService == null)
        {
            return result;
        }

        try
        {
            var adaptations = await _personalityAdaptationService.ProcessAdaptationsAsync(context, result, ct);
            if (adaptations != null && adaptations.Count > 0)
            {
                return result with {
                    PersonalityAdaptations = adaptations,
                    PersonalityAdaptation = adaptations.FirstOrDefault(a => a.AdaptationTriggered) ?? adaptations.FirstOrDefault()
                };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PersonalityAdaptationIdempotencyConflictException ex)
        {
            _logger.LogWarning(ex,
                "[CharacterCognitiveCycleService] Idempotency conflict detected in personality adaptation for CharacterId={CharacterId}, ExecutionId={ExecutionId}. {Message}",
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
                relationshipFeedback: result.RelationshipFeedback,
                personalitySnapshot: result.PersonalitySnapshot,
                personalityAdaptation: null,
                message: ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[CharacterCognitiveCycleService] Failed to process personality adaptation for CharacterId={CharacterId}, CycleId={CycleId}. State transition remains committed.",
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
            AutonomousCognitiveEvent autoEvt => new CharacterPerceptionStimulus(
                type: PerceptionStimulusType.Autonomous,
                source: string.IsNullOrWhiteSpace(autoEvt.Source) ? "Autonomous" : autoEvt.Source,
                content: string.IsNullOrWhiteSpace(autoEvt.EventName) ? "AutonomousTick" : autoEvt.EventName,
                occurredAtUtc: autoEvt.OccurredAtUtc,
                category: "Autonomous"
            ),
            _ => throw new NotSupportedException($"Unsupported cognitive event type: {cognitiveEvent.GetType().Name}")
        };
    }
}
