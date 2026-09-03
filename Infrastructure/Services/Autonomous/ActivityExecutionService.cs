using Application.Contracts.Autonomous;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Autonomous;

public sealed class ActivityExecutionService : IActivityExecutionService
{
    private readonly CoreDbContext _dbContext;
    private readonly IGoalProgressService _goalProgressService;
    private readonly ISceneCompositionPipelineService _sceneCompositionPipeline;
    private readonly ISceneVisualStateReader _visualStateReader;
    private readonly ICharacterStateTransitionService _stateTransitionService;
    private readonly ILogger<ActivityExecutionService> _logger;

    public ActivityExecutionService(
        CoreDbContext dbContext,
        IGoalProgressService goalProgressService,
        ISceneCompositionPipelineService sceneCompositionPipeline,
        ISceneVisualStateReader visualStateReader,
        ICharacterStateTransitionService stateTransitionService,
        ILogger<ActivityExecutionService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _goalProgressService = goalProgressService ?? throw new ArgumentNullException(nameof(goalProgressService));
        _sceneCompositionPipeline = sceneCompositionPipeline ?? throw new ArgumentNullException(nameof(sceneCompositionPipeline));
        _visualStateReader = visualStateReader ?? throw new ArgumentNullException(nameof(visualStateReader));
        _stateTransitionService = stateTransitionService ?? throw new ArgumentNullException(nameof(stateTransitionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ActivityExecutionResult> ExecuteActivityAsync(
        ActivityExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request, nameof(request));
        ct.ThrowIfCancellationRequested();

        var character = request.Character;
        var candidate = request.Candidate;
        var now = request.CurrentTime;
        var timeBucket = request.TimeBucket;
        var executionId = request.ExecutionId ?? Guid.NewGuid();

        // Authoritative state requirement: load persisted state or initialize new persistent entity
        var persistedState = await _dbContext.CharacterStates
            .FirstOrDefaultAsync(s => s.CharacterId == character.Id, ct);

        if (persistedState == null)
        {
            persistedState = new CharacterState(character.Id, initializedAtUtc: now);
            await _dbContext.CharacterStates.AddAsync(persistedState, ct);
        }

        var currentState = persistedState.ToSnapshot();

        // 1. Create CharacterActivity entity
        var activity = new CharacterActivity(
            characterId: character.Id,
            activityType: candidate.ActivityType,
            location: candidate.Location,
            timeBucket: timeBucket,
            decisionFingerprint: candidate.DecisionFingerprint,
            source: CharacterActivitySource.Autonomous,
            priority: candidate.Priority,
            durationMinutes: candidate.DurationMinutes,
            shouldCreateVisualMoment: candidate.ShouldCreateVisualMoment,
            reason: candidate.Reason,
            startedAt: now,
            goalId: candidate.GoalId,
            status: CharacterActivityStatus.Started,
            now: now
        );

        // 0. Pre-check idempotency on read-only path before opening transaction
        var existingActivity = await _dbContext.CharacterActivities
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.CharacterId == character.Id && a.TimeBucket == timeBucket, ct);

        var existingTransition = await _dbContext.CharacterStateTransitions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.CharacterId == character.Id && t.ExecutionId == executionId, ct);

        if (existingActivity != null || existingTransition != null)
        {
            var refreshedState = await _dbContext.CharacterStates
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.CharacterId == character.Id, ct);

            return new ActivityExecutionResult(
                Success: true,
                IsDuplicateSuppressed: true,
                ExecutionId: executionId,
                Activity: null,
                NewState: refreshedState?.ToSnapshot() ?? currentState,
                GoalResult: null,
                VisualMomentCreated: false,
                SceneIntentId: null,
                SceneSpecificationId: null,
                Message: "Idempotent duplicate activity suppressed."
            );
        }

        // P0-2: ATOMIC TRANSACTION BOUNDARY: Activity + State Transition commit together
        var isOuterTx = _dbContext.Database.CurrentTransaction == null;
        await using var tx = isOuterTx ? await _dbContext.Database.BeginTransactionAsync(ct) : null;

        try
        {
            // Stage activity entity
            await _dbContext.CharacterActivities.AddAsync(activity, ct);

            // Stage state transition
            var outcomeDelta = CharacterActivityOutcomeStatePolicy.CalculateOutcomeDelta(candidate.ActivityType);
            var transitionContext = new Application.Common.StateTransitionContext(
                ExecutionId: executionId,
                SourceType: "ActivityOutcome",
                SourceId: activity.Id.ToString(),
                Reason: $"Completed activity {candidate.ActivityType}"
            );

            _stateTransitionService.StageTransition(persistedState, outcomeDelta, transitionContext, now);

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
                "[ActivityExecutionService] Idempotent duplicate activity suppressed for ExecutionId={ExecutionId}, CharacterId={CharacterId}, TimeBucket={TimeBucket}",
                executionId, character.Id, timeBucket);

            return new ActivityExecutionResult(
                Success: true,
                IsDuplicateSuppressed: true,
                ExecutionId: executionId,
                Activity: null,
                NewState: currentState,
                GoalResult: null,
                VisualMomentCreated: false,
                SceneIntentId: null,
                SceneSpecificationId: null,
                Message: "Idempotent duplicate activity suppressed."
            );
        }
        catch
        {
            if (tx != null) await tx.RollbackAsync(ct);
            _dbContext.ChangeTracker.Clear();
            throw;
        }

        var newState = persistedState.ToSnapshot();

        // 4. Record Goal Progress if GoalId is attached
        GoalProgressResult? goalResult = null;
        if (candidate.GoalId.HasValue)
        {
            double contributionValue = candidate.DurationMinutes > 0 ? candidate.DurationMinutes / 2.0 : 10.0;
            try
            {
                goalResult = await _goalProgressService.RecordContributionAsync(
                    goalId: candidate.GoalId.Value,
                    activityId: activity.Id,
                    contributionValue: contributionValue,
                    now: now,
                    ct: ct
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ActivityExecutionService] Failed recording goal contribution for ExecutionId={ExecutionId}, GoalId={GoalId}, ActivityId={ActivityId}",
                    executionId, candidate.GoalId.Value, activity.Id);
            }
        }

        // 5. Evaluate Visual Moment Trigger (Candidate Policy OR Milestone Achievement)
        bool shouldTriggerVisual = candidate.ShouldCreateVisualMoment ||
            (goalResult != null && (goalResult.MilestoneCompleted || goalResult.GoalCompleted));

        Guid? sceneIntentId = null;
        Guid? sceneSpecificationId = null;

        if (shouldTriggerVisual)
        {
            try
            {
                // Authoritative Scene Revision & Visual State Resolution
                var latestVisualState = await _visualStateReader.GetLatestByCharacterIdAsync(character.Id, ct);
                var currentVisualState = latestVisualState?.CharacterState ?? request.CurrentVisualState;
                int sceneRevision = latestVisualState != null ? latestVisualState.SceneRevision : request.SceneRevision;

                var sceneIntent = CharacterActivitySceneIntentMapper.MapToSceneIntent(
                    activity: activity,
                    candidate: candidate,
                    currentVisualState: currentVisualState,
                    sessionId: null,
                    turnId: null
                );

                activity.LinkSceneIntent(sceneIntent.Id);
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
                _logger.LogError(ex, "[ActivityExecutionService] Visual moment composition failed for ExecutionId={ExecutionId}, ActivityId={ActivityId}. Activity execution remains successful.", executionId, activity.Id);
            }
        }

        // 6. Complete Activity
        activity.Complete(now.AddMinutes(candidate.DurationMinutes));
        try
        {
            await _dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ActivityExecutionService] Non-critical warning completing activity record for ExecutionId={ExecutionId}, ActivityId={ActivityId}", executionId, activity.Id);
        }

        _logger.LogInformation(
            "[ActivityExecutionService] Successfully executed activity. ExecutionId={ExecutionId}, CharacterId={CharacterId}, ActivityType={ActivityType}, DecisionFingerprint={DecisionFingerprint}, GoalId={GoalId}, VisualMomentTriggered={VisualMomentTriggered}, SceneRevision={SceneRevision}, Outcome={Outcome}",
            executionId, character.Id, activity.ActivityType, activity.DecisionFingerprint, candidate.GoalId, sceneSpecificationId.HasValue || sceneIntentId.HasValue, request.SceneRevision, "Success");

        return new ActivityExecutionResult(
            Success: true,
            IsDuplicateSuppressed: false,
            ExecutionId: executionId,
            Activity: activity,
            NewState: newState,
            GoalResult: goalResult,
            VisualMomentCreated: sceneSpecificationId.HasValue || sceneIntentId.HasValue,
            SceneIntentId: sceneIntentId,
            SceneSpecificationId: sceneSpecificationId,
            Message: "Activity executed successfully."
        );
    }

    public static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        return DbConstraintClassifier.IsUniqueViolation(
            ex,
            expectedPostgresConstraints:
            [
                "IX_CharacterActivities_CharacterId_TimeBucket",
                "IX_CharacterStateTransitions_CharacterId_ExecutionId"
            ],
            expectedSqliteTable: "CharacterActivities",
            expectedSqliteColumns: ["CharacterId", "TimeBucket"])
            || DbConstraintClassifier.IsUniqueViolation(
                ex,
                expectedPostgresConstraints: ["IX_CharacterStateTransitions_CharacterId_ExecutionId"],
                expectedSqliteTable: "CharacterStateTransitions",
                expectedSqliteColumns: ["CharacterId", "ExecutionId"]);
    }
}
