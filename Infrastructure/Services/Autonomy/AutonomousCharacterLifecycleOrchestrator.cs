using Application.Contracts.Activities;
using Application.Contracts.Autonomous;
using Application.Contracts.Autonomy;
using Application.Contracts.Reactions;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Autonomy;

/// <summary>
/// Authoritative, thin orchestrator coordinating the autonomous lifecycle tick of a character.
/// Flow: Atomic Tick Claim -> Context Load -> World Event Reaction (if any) -> Autonomous Decision -> Activity Execution -> Completion.
/// Implements database-authoritative idempotency on (CharacterId, TimeBucket) with controlled retry for Failed ticks.
/// </summary>
public sealed class AutonomousCharacterLifecycleOrchestrator : IAutonomousCharacterLifecycleOrchestrator
{
    private readonly ProjectDbContext _dbContext;
    private readonly IAutonomousCharacterContextLoader _contextLoader;
    private readonly IAutonomousDecisionService _decisionService;
    private readonly IActivityExecutionService _activityExecutionService;
    private readonly ICharacterReactionExecutionService _reactionService;
    private readonly ILogger<AutonomousCharacterLifecycleOrchestrator> _logger;

    public AutonomousCharacterLifecycleOrchestrator(
        ProjectDbContext dbContext,
        IAutonomousCharacterContextLoader contextLoader,
        IAutonomousDecisionService decisionService,
        IActivityExecutionService activityExecutionService,
        ICharacterReactionExecutionService reactionService,
        ILogger<AutonomousCharacterLifecycleOrchestrator> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _contextLoader = contextLoader ?? throw new ArgumentNullException(nameof(contextLoader));
        _decisionService = decisionService ?? throw new ArgumentNullException(nameof(decisionService));
        _activityExecutionService = activityExecutionService ?? throw new ArgumentNullException(nameof(activityExecutionService));
        _reactionService = reactionService ?? throw new ArgumentNullException(nameof(reactionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AutonomyTickResult> ExecuteTickAsync(
        AutonomyTickRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.CharacterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(request));

        if (request.ExecutionId == Guid.Empty)
            throw new ArgumentException("ExecutionId cannot be empty.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.TimeBucket))
            throw new ArgumentException("TimeBucket cannot be empty.", nameof(request));

        var now = request.CurrentTime != default ? request.CurrentTime : DateTime.UtcNow;

        _logger.LogInformation(
            "[AutonomousCharacterLifecycleOrchestrator] Starting autonomous tick for CharacterId={CharacterId}, TimeBucket={TimeBucket}, ExecutionId={ExecutionId}",
            request.CharacterId, request.TimeBucket, request.ExecutionId);

        // 1. Acquire Authoritative Tick via Database Unique Constraint Race
        var tick = CharacterAutonomyTick.Create(
            characterId: request.CharacterId,
            executionId: request.ExecutionId,
            timeBucket: request.TimeBucket,
            startedAt: now,
            worldEventId: request.WorldEventId,
            correlationId: request.CorrelationId
        );

        try
        {
            await _dbContext.CharacterAutonomyTicks.AddAsync(tick, ct);
            await _dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Detach the failed insert from EF Core ChangeTracker so it doesn't conflict with subsequent operations
            _dbContext.Entry(tick).State = EntityState.Detached;

            // Unique violation on (CharacterId, TimeBucket): Check if existing tick is Failed (eligible for controlled retry) or Completed/Running (suppressed)
            var existingTick = await _dbContext.CharacterAutonomyTicks
                .FirstOrDefaultAsync(t => t.CharacterId == request.CharacterId && t.TimeBucket == request.TimeBucket.Trim(), ct);

            if (existingTick == null || existingTick.Status != AutonomyTickStatus.Failed)
            {
                _logger.LogInformation(
                    "[AutonomousCharacterLifecycleOrchestrator] Duplicate tick suppressed for CharacterId={CharacterId}, TimeBucket={TimeBucket}, ExecutionId={ExecutionId}",
                    request.CharacterId, request.TimeBucket, request.ExecutionId);

                return new AutonomyTickResult(
                    Success: true,
                    IsDuplicateSuppressed: true,
                    ExecutionId: request.ExecutionId,
                    Tick: null,
                    ReactionResult: null,
                    ActivityResult: null,
                    Message: "Idempotent duplicate tick suppressed by database unique constraint."
                );
            }

            // Controlled Retry: Atomic re-acquisition of Failed tick using optimistic concurrency token (Version)
            try
            {
                existingTick.ReclaimForRetry(request.ExecutionId, now, request.WorldEventId, request.CorrelationId);
                await _dbContext.SaveChangesAsync(ct);
                tick = existingTick;
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogInformation(
                    "[AutonomousCharacterLifecycleOrchestrator] Concurrent retry claim lost for CharacterId={CharacterId}, TimeBucket={TimeBucket}",
                    request.CharacterId, request.TimeBucket);

                return new AutonomyTickResult(
                    Success: true,
                    IsDuplicateSuppressed: true,
                    ExecutionId: request.ExecutionId,
                    Tick: null,
                    ReactionResult: null,
                    ActivityResult: null,
                    Message: "Concurrent retry claim suppressed."
                );
            }
        }

        ReactionExecutionResult? reactionResult = null;
        ActivityExecutionResult? activityResult = null;

        try
        {
            // 2. Load Domain Context via IAutonomousCharacterContextLoader
            var context = await _contextLoader.LoadContextAsync(request.CharacterId, now, ct);
            if (context == null)
            {
                tick.Fail(DateTime.UtcNow, $"Character with ID {request.CharacterId} not found.");
                await _dbContext.SaveChangesAsync(ct);
                return new AutonomyTickResult(
                    Success: false,
                    IsDuplicateSuppressed: false,
                    ExecutionId: request.ExecutionId,
                    Tick: tick,
                    ReactionResult: null,
                    ActivityResult: null,
                    Message: $"Character {request.CharacterId} not found."
                );
            }

            var currentState = context.CurrentState;

            // 3. World Event Reaction (if provided)
            if (request.WorldEventId.HasValue)
            {
                var worldEvent = await _dbContext.CharacterWorldEvents.FindAsync(new object?[] { request.WorldEventId.Value }, ct);
                if (worldEvent != null)
                {
                    var reactionRequest = new ReactionExecutionRequest(
                        WorldEvent: worldEvent,
                        Character: context.Character,
                        ExecutionId: request.ExecutionId,
                        CurrentTime: now,
                        CurrentState: currentState,
                        CurrentVisualState: context.CurrentVisualState,
                        CurrentGoals: context.GoalSnapshotsForReaction,
                        SceneRevision: context.SceneRevision
                    );

                    reactionResult = await _reactionService.ExecuteReactionAsync(reactionRequest, ct);
                    if (reactionResult.Reaction != null)
                    {
                        tick.LinkReaction(reactionResult.Reaction.Id);
                    }
                    if (reactionResult.NewState != null)
                    {
                        currentState = reactionResult.NewState;
                    }
                }
            }

            // 4. Autonomous Decision
            var decisionRequest = new AutonomousDecisionRequest(
                CharacterId: context.Character.Id,
                CurrentTime: now,
                CurrentLocation: context.CurrentLocation,
                TimeBucket: request.TimeBucket,
                CurrentVisualState: context.CurrentVisualState,
                StateSnapshot: currentState,
                RecentActivities: context.RecentActivities,
                RecentVisualMemories: context.RecentVisualMemories,
                PersonalityPrompt: context.Character.PersonalityPrompt,
                WorldDescription: context.Character.WorldDescription,
                ActiveGoals: context.ActiveGoals,
                Goals: context.GoalSnapshots,
                SceneRevision: context.SceneRevision
            );

            var decision = await _decisionService.DecideNextActionAsync(decisionRequest, ct);

            // 5. Execute Activity (if decided)
            if (decision.Action == AutonomousDecisionAction.PerformActivity && decision.Candidate != null)
            {
                var executionRequest = new ActivityExecutionRequest(
                    Character: context.Character,
                    Candidate: decision.Candidate,
                    CurrentTime: now,
                    TimeBucket: request.TimeBucket,
                    ExecutionId: request.ExecutionId,
                    CurrentVisualState: context.CurrentVisualState,
                    CurrentState: currentState,
                    SceneRevision: context.SceneRevision
                );

                activityResult = await _activityExecutionService.ExecuteActivityAsync(executionRequest, ct);
            }

            // 6. Complete Tick Atomically
            tick.Complete(
                completedAt: DateTime.UtcNow,
                activityId: activityResult?.Activity?.Id,
                sceneSpecificationId: activityResult?.SceneSpecificationId,
                decisionFingerprint: decision?.Candidate?.DecisionFingerprint
            );

            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[AutonomousCharacterLifecycleOrchestrator] Successfully completed tick for CharacterId={CharacterId}, TimeBucket={TimeBucket}, ExecutionId={ExecutionId}",
                context.Character.Id, request.TimeBucket, request.ExecutionId);

            return new AutonomyTickResult(
                Success: true,
                IsDuplicateSuppressed: false,
                ExecutionId: request.ExecutionId,
                Tick: tick,
                ReactionResult: reactionResult,
                ActivityResult: activityResult,
                Message: "Autonomous character lifecycle tick executed successfully."
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[AutonomousCharacterLifecycleOrchestrator] Error during autonomous tick for CharacterId={CharacterId}, TimeBucket={TimeBucket}, ExecutionId={ExecutionId}",
                request.CharacterId, request.TimeBucket, request.ExecutionId);

            try
            {
                tick.Fail(DateTime.UtcNow, ex.Message);
                await _dbContext.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx,
                    "[AutonomousCharacterLifecycleOrchestrator] Failed updating tick to Failed status for ExecutionId={ExecutionId}",
                    request.ExecutionId);
            }

            return new AutonomyTickResult(
                Success: false,
                IsDuplicateSuppressed: false,
                ExecutionId: request.ExecutionId,
                Tick: tick,
                ReactionResult: reactionResult,
                ActivityResult: activityResult,
                Message: ex.Message
            );
        }
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
                        pg.ConstraintName.Contains("CharacterAutonomyTicks", StringComparison.OrdinalIgnoreCase) ||
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
                        (innerMsg.Contains("CharacterAutonomyTicks", StringComparison.OrdinalIgnoreCase) ||
                         innerMsg.Contains("TimeBucket", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }

            inner = inner.InnerException;
        }

        var msg = (ex.InnerException?.Message ?? "") + " " + (ex.Message ?? "");
        if (msg.Contains("IX_CharacterAutonomyTicks_CharacterId_TimeBucket", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("UNIQUE constraint failed: CharacterAutonomyTicks.CharacterId, CharacterAutonomyTicks.TimeBucket", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("23505", StringComparison.OrdinalIgnoreCase) ||
            (msg.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) && msg.Contains("CharacterAutonomyTicks", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }
}
