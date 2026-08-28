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
    private readonly ILogger<ActivityExecutionService> _logger;

    public ActivityExecutionService(
        CoreDbContext dbContext,
        IGoalProgressService goalProgressService,
        ISceneCompositionPipelineService sceneCompositionPipeline,
        ISceneVisualStateReader visualStateReader,
        ILogger<ActivityExecutionService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _goalProgressService = goalProgressService ?? throw new ArgumentNullException(nameof(goalProgressService));
        _sceneCompositionPipeline = sceneCompositionPipeline ?? throw new ArgumentNullException(nameof(sceneCompositionPipeline));
        _visualStateReader = visualStateReader ?? throw new ArgumentNullException(nameof(visualStateReader));
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
        var currentState = request.CurrentState ?? CharacterStateSnapshot.CreateDefault();

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

        // 2. Persist Activity with Distributed Concurrency Guard
        try
        {
            await _dbContext.CharacterActivities.AddAsync(activity, ct);
            await _dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            if (IsUniqueConstraintViolation(ex))
            {
                _logger.LogInformation(ex, "[ActivityExecutionService] Idempotent duplicate activity suppressed for ExecutionId={ExecutionId}, CharacterId={CharacterId}, TimeBucket={TimeBucket}",
                    executionId, character.Id, timeBucket);

                _dbContext.Entry(activity).State = EntityState.Detached;

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

            _logger.LogError(ex, "[ActivityExecutionService] Non-duplicate database failure saving activity for ExecutionId={ExecutionId}, CharacterId={CharacterId}, TimeBucket={TimeBucket}",
                executionId, character.Id, timeBucket);
            throw;
        }

        // 3. Apply Deterministic State Outcome
        var newState = CharacterActivityOutcomePolicy.ApplyOutcome(currentState, candidate.ActivityType);

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
        var inner = ex.InnerException;
        while (inner != null)
        {
            if (inner is Npgsql.PostgresException pg)
            {
                if (pg.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
                {
                    if (string.IsNullOrWhiteSpace(pg.ConstraintName) ||
                        pg.ConstraintName.Contains("CharacterActivities", StringComparison.OrdinalIgnoreCase) ||
                        pg.ConstraintName.Contains("TimeBucket", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }

            var sqlStateProp = inner.GetType().GetProperty("SqlState");
            if (sqlStateProp != null)
            {
                var sqlState = sqlStateProp.GetValue(inner)?.ToString();
                if (sqlState == Npgsql.PostgresErrorCodes.UniqueViolation || sqlState == "23505")
                {
                    return true;
                }
            }

            var sqliteErrProp = inner.GetType().GetProperty("SqliteErrorCode");
            if (sqliteErrProp != null)
            {
                var errCode = sqliteErrProp.GetValue(inner);
                if (errCode is int code && code == 19)
                {
                    var innerMsg = inner.Message ?? "";
                    if (innerMsg.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) &&
                        (innerMsg.Contains("CharacterActivities", StringComparison.OrdinalIgnoreCase) || innerMsg.Contains("TimeBucket", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }

            inner = inner.InnerException;
        }

        var msg = (ex.InnerException?.Message ?? "") + " " + (ex.Message ?? "");
        if (msg.Contains("IX_CharacterActivities_CharacterId_TimeBucket", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("UNIQUE constraint failed: CharacterActivities.CharacterId, CharacterActivities.TimeBucket", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("23505", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) ||
            (msg.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) && msg.Contains("TimeBucket", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }
}
