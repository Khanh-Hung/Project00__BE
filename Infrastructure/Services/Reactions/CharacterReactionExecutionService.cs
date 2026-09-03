using Application.Contracts.Activities;
using Application.Contracts.Autonomous;
using Application.Contracts.Reactions;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services.Autonomous;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Reactions;

public sealed class CharacterReactionExecutionService : ICharacterReactionExecutionService
{
    private readonly CoreDbContext _dbContext;
    private readonly IGoalProgressService _goalProgressService;
    private readonly IActivityExecutionService _activityExecutionService;
    private readonly ISceneCompositionPipelineService _sceneCompositionPipeline;
    private readonly ISceneVisualStateReader _visualStateReader;
    private readonly ICharacterStateTransitionStager _stateTransitionService;
    private readonly ILogger<CharacterReactionExecutionService> _logger;

    public CharacterReactionExecutionService(
        CoreDbContext dbContext,
        IGoalProgressService goalProgressService,
        IActivityExecutionService activityExecutionService,
        ISceneCompositionPipelineService sceneCompositionPipeline,
        ISceneVisualStateReader visualStateReader,
        ICharacterStateTransitionStager stateTransitionService,
        ILogger<CharacterReactionExecutionService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _goalProgressService = goalProgressService ?? throw new ArgumentNullException(nameof(goalProgressService));
        _activityExecutionService = activityExecutionService ?? throw new ArgumentNullException(nameof(activityExecutionService));
        _sceneCompositionPipeline = sceneCompositionPipeline ?? throw new ArgumentNullException(nameof(sceneCompositionPipeline));
        _visualStateReader = visualStateReader ?? throw new ArgumentNullException(nameof(visualStateReader));
        _stateTransitionService = stateTransitionService ?? throw new ArgumentNullException(nameof(stateTransitionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ReactionExecutionResult> ExecuteReactionAsync(
        ReactionExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request, nameof(request));
        ct.ThrowIfCancellationRequested();

        var character = request.Character;
        var worldEvent = request.WorldEvent;
        var executionId = request.ExecutionId;
        var now = request.CurrentTime;

        // Authoritative state requirement: load persisted state from database or initialize persistent entity
        var persistedState = await _dbContext.CharacterStates
            .FirstOrDefaultAsync(s => s.CharacterId == character.Id, ct);
        if (persistedState == null)
        {
            persistedState = new CharacterState(character.Id, initializedAtUtc: now);
            await _dbContext.CharacterStates.AddAsync(persistedState, ct);
        }

        var currentState = persistedState.ToSnapshot();

        // 1. Evaluate Pure Perception & Pure Reaction Policies
        var perception = CharacterPerceptionPolicy.EvaluatePerception(
            worldEvent: worldEvent,
            state: currentState,
            goals: request.CurrentGoals,
            currentActivity: request.CurrentActivity,
            currentVisualState: request.CurrentVisualState
        );

        var reaction = CharacterReactionPolicy.EvaluateReaction(
            perception: perception,
            worldEvent: worldEvent,
            state: currentState,
            goals: request.CurrentGoals,
            currentActivity: request.CurrentActivity,
            currentSceneState: request.CurrentVisualState
        );

        // 2. Prepare Reaction Entity
        var reactionEntity = CharacterWorldEventReaction.Create(
            characterId: character.Id,
            worldEventId: worldEvent.Id,
            executionId: executionId,
            perceptionType: perception.PerceptionType,
            priority: reaction.Priority,
            reactionReason: reaction.ReactionReason,
            moodDelta: reaction.MoodDelta,
            energyDelta: reaction.EnergyDelta,
            stressDelta: reaction.StressDelta,
            hungerDelta: reaction.HungerDelta,
            socialNeedDelta: reaction.SocialNeedDelta,
            confidenceDelta: reaction.ConfidenceDelta,
            relationshipDelta: reaction.RelationshipDelta,
            goalId: reaction.GoalImpact?.GoalId,
            goalContribution: reaction.GoalImpact?.ContributionValue,
            activityTriggered: reaction.ShouldTriggerActivity,
            triggeredActivityType: reaction.ActivityIntentType,
            visualMomentCreated: reaction.ShouldTriggerVisualMoment,
            processedAt: now
        );

        // 0. Pre-check idempotency on read-only path before opening transaction
        var existingReaction = await _dbContext.CharacterWorldEventReactions
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.CharacterId == character.Id && r.WorldEventId == worldEvent.Id, ct);

        var existingTransition = await _dbContext.CharacterStateTransitions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.CharacterId == character.Id && t.ExecutionId == executionId, ct);

        if (existingReaction != null || existingTransition != null)
        {
            var refreshedState = await _dbContext.CharacterStates
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.CharacterId == character.Id, ct);

            return new ReactionExecutionResult(
                Success: true,
                IsDuplicateSuppressed: true,
                ExecutionId: executionId,
                Reaction: null,
                NewState: refreshedState?.ToSnapshot() ?? currentState,
                MemoryCreated: false,
                MemoryId: null,
                GoalContributed: false,
                GoalId: null,
                GoalContributionValue: null,
                VisualMomentCreated: false,
                SceneIntentId: null,
                SceneSpecificationId: null,
                ActivityTriggered: false,
                Message: "Idempotent duplicate reaction suppressed."
            );
        }

        // P0-2: ATOMIC TRANSACTION BOUNDARY: Reaction + State Transition commit together
        var isOuterTx = _dbContext.Database.CurrentTransaction == null;
        await using var tx = isOuterTx ? await _dbContext.Database.BeginTransactionAsync(ct) : null;

        try
        {
            // Stage reaction entity
            await _dbContext.CharacterWorldEventReactions.AddAsync(reactionEntity, ct);

            // Stage state transition
            var stateDelta = new CharacterStateDelta(
                hungerDelta: reaction.HungerDelta,
                energyDelta: reaction.EnergyDelta,
                moodDelta: reaction.MoodDelta != 0 ? reaction.MoodDelta : (reaction.MoodIntensityDelta != 0 ? reaction.MoodIntensityDelta : 0m),
                stressDelta: reaction.StressDelta,
                socialNeedDelta: reaction.SocialNeedDelta,
                comfortDelta: 0m
            );

            var transitionContext = new Application.Common.StateTransitionContext(
                ExecutionId: executionId,
                SourceType: "Reaction",
                SourceId: worldEvent.Id.ToString(),
                Reason: reaction.ReactionReason
            );

            _stateTransitionService.StageTransition(persistedState, stateDelta, transitionContext, now);

            // Commit atomic boundary
            await _dbContext.SaveChangesAsync(ct);
            if (tx != null)
            {
                await tx.CommitAsync(ct);
            }
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            if (tx != null) await tx.RollbackAsync(ct);
            _dbContext.ChangeTracker.Clear();

            _logger.LogInformation(
                ex,
                "[CharacterReactionExecutionService] Idempotent duplicate reaction suppressed for ExecutionId={ExecutionId}, CharacterId={CharacterId}, WorldEventId={WorldEventId}",
                executionId, character.Id, worldEvent.Id);

            return new ReactionExecutionResult(
                Success: true,
                IsDuplicateSuppressed: true,
                ExecutionId: executionId,
                Reaction: null,
                NewState: currentState,
                MemoryCreated: false,
                MemoryId: null,
                GoalContributed: false,
                GoalId: null,
                GoalContributionValue: null,
                VisualMomentCreated: false,
                SceneIntentId: null,
                SceneSpecificationId: null,
                ActivityTriggered: false,
                Message: "Idempotent duplicate reaction suppressed."
            );
        }
        catch
        {
            if (tx != null) await tx.RollbackAsync(ct);
            _dbContext.ChangeTracker.Clear();
            throw;
        }

        var newState = persistedState.ToSnapshot();

        // 5. Memory Candidate Formation (reusing existing CharacterMemory)
        Guid? memoryId = null;
        if (reaction.MemoryCandidate != null)
        {
            try
            {
                var memory = CharacterMemory.Create(
                    characterId: character.Id,
                    userId: character.Id,
                    content: reaction.MemoryCandidate.Content,
                    type: reaction.MemoryCandidate.Type,
                    importance: reaction.MemoryCandidate.Importance,
                    confidence: reaction.MemoryCandidate.Confidence
                );

                await _dbContext.CharacterMemories.AddAsync(memory, ct);
                await _dbContext.SaveChangesAsync(ct);
                memoryId = memory.Id;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CharacterReactionExecutionService] Failed to persist memory candidate for CharacterId={CharacterId}, EventId={EventId}",
                    character.Id, worldEvent.Id);
            }
        }

        // 6. Goal Impact (reusing existing GoalProgressService)
        bool goalContributed = false;
        if (reaction.GoalImpact != null)
        {
            try
            {
                var goal = await _dbContext.CharacterGoals.FirstOrDefaultAsync(g => g.Id == reaction.GoalImpact.GoalId, ct);
                if (goal != null && goal.Status == CharacterGoalStatus.Active)
                {
                    goal.RecordProgress(reaction.GoalImpact.ContributionValue, now);
                    await _dbContext.SaveChangesAsync(ct);
                    goalContributed = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CharacterReactionExecutionService] Failed to apply goal progress for GoalId={GoalId}", reaction.GoalImpact.GoalId);
            }
        }

        // 7. Visual Moment Generation (reusing Scene Composition Pipeline)
        Guid? sceneIntentId = null;
        Guid? sceneSpecificationId = null;
        if (reaction.ShouldTriggerVisualMoment)
        {
            try
            {
                var latestVisualState = await _visualStateReader.GetLatestByCharacterIdAsync(character.Id, ct);
                var currentVisualState = latestVisualState?.CharacterState ?? request.CurrentVisualState;
                int baseRevision = latestVisualState != null ? latestVisualState.SceneRevision : request.SceneRevision;

                var maxExistingRevision = await _dbContext.SceneSpecifications
                    .Where(s => s.CharacterId == character.Id)
                    .Select(s => (int?)s.SceneRevision)
                    .MaxAsync(ct) ?? 0;
                int sceneRevision = Math.Max(baseRevision, maxExistingRevision + 1);

                var candidate = new CharacterActivityCandidate(
                    ActivityType: reaction.ActivityIntentType ?? CharacterActivityType.Socializing,
                    Location: currentVisualState?.Location ?? "Common Area",
                    Reason: reaction.ReactionReason,
                    Priority: reaction.VisualPriority ?? ActivityPriority.Normal,
                    DurationMinutes: 15,
                    ShouldCreateVisualMoment: true,
                    Confidence: 0.95f,
                    ActionHint: reaction.ActionHint ?? "reacting expressively to event",
                    PoseHint: reaction.PoseHint,
                    EnvironmentHint: reaction.EnvironmentHint,
                    DecisionFingerprint: $"reaction-{worldEvent.Id}"
                );

                var tempActivity = new CharacterActivity(
                    characterId: character.Id,
                    activityType: candidate.ActivityType,
                    location: candidate.Location,
                    timeBucket: $"reaction-{now:yyyyMMddHHmm}",
                    decisionFingerprint: candidate.DecisionFingerprint,
                    source: CharacterActivitySource.Autonomous,
                    priority: candidate.Priority,
                    durationMinutes: candidate.DurationMinutes,
                    shouldCreateVisualMoment: true,
                    reason: candidate.Reason,
                    startedAt: now,
                    status: CharacterActivityStatus.Started,
                    now: now
                );

                var sceneIntent = CharacterActivitySceneIntentMapper.MapToSceneIntent(
                    activity: tempActivity,
                    candidate: candidate,
                    currentVisualState: currentVisualState,
                    sessionId: null,
                    turnId: null
                );

                sceneIntentId = sceneIntent.Id;

                var pipelineResult = await _sceneCompositionPipeline.ExecuteAsync(
                    intent: sceneIntent,
                    generationProfile: GenerationProfile.CreateDefault(),
                    sceneRevision: sceneRevision,
                    ct: ct
                );

                if (pipelineResult?.SceneSpecification != null)
                {
                    sceneSpecificationId = pipelineResult.SceneSpecification.Id;
                    await _dbContext.SceneSpecifications.AddAsync(pipelineResult.SceneSpecification, ct);
                    await _dbContext.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CharacterReactionExecutionService] Visual composition failed for EventId={EventId}. Reaction remains successful.", worldEvent.Id);
            }
        }

        _logger.LogInformation(
            "[CharacterReactionExecutionService] Successfully executed reaction. ExecutionId={ExecutionId}, CharacterId={CharacterId}, WorldEventId={WorldEventId}, PerceptionType={PerceptionType}, Priority={Priority}, MemoryCreated={MemoryCreated}, GoalContributed={GoalContributed}, VisualMomentCreated={VisualMomentCreated}",
            executionId, character.Id, worldEvent.Id, perception.PerceptionType, reaction.Priority, memoryId.HasValue, goalContributed, sceneSpecificationId.HasValue);

        return new ReactionExecutionResult(
            Success: true,
            IsDuplicateSuppressed: false,
            ExecutionId: executionId,
            Reaction: reactionEntity,
            NewState: newState,
            MemoryCreated: memoryId.HasValue,
            MemoryId: memoryId,
            GoalContributed: goalContributed,
            GoalId: reaction.GoalImpact?.GoalId,
            GoalContributionValue: reaction.GoalImpact?.ContributionValue,
            VisualMomentCreated: sceneSpecificationId.HasValue || sceneIntentId.HasValue,
            SceneIntentId: sceneIntentId,
            SceneSpecificationId: sceneSpecificationId,
            ActivityTriggered: reaction.ShouldTriggerActivity,
            Message: "Reaction executed successfully."
        );
    }

    public static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        return DbConstraintClassifier.IsUniqueViolation(
            ex,
            expectedPostgresConstraints:
            [
                "IX_CharacterWorldEventReactions_WorldEventId_CharacterId",
                "IX_CharacterStateTransitions_CharacterId_ExecutionId"
            ],
            expectedSqliteTable: "CharacterWorldEventReactions",
            expectedSqliteColumns: ["WorldEventId", "CharacterId"])
            || DbConstraintClassifier.IsUniqueViolation(
                ex,
                expectedPostgresConstraints: ["IX_CharacterStateTransitions_CharacterId_ExecutionId"],
                expectedSqliteTable: "CharacterStateTransitions",
                expectedSqliteColumns: ["CharacterId", "ExecutionId"]);
    }
}
