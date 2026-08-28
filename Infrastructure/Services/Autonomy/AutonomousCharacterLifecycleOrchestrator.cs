using System.Text.Json;
using Application.Contracts.Activities;
using Application.Contracts.Autonomous;
using Application.Contracts.Autonomy;
using Application.Contracts.Goals;
using Application.Contracts.Reactions;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Autonomy;

/// <summary>
/// Authoritative, thin orchestrator coordinating the autonomous lifecycle tick of a character.
/// Flow: Atomic Tick Claim -> Context Load -> World Event Reaction (if any) -> Autonomous Decision -> Activity Execution -> Completion.
/// Implements database-authoritative idempotency on (CharacterId, TimeBucket).
/// </summary>
public sealed class AutonomousCharacterLifecycleOrchestrator : IAutonomousCharacterLifecycleOrchestrator
{
    private readonly ProjectDbContext _dbContext;
    private readonly IAutonomousDecisionService _decisionService;
    private readonly IActivityExecutionService _activityExecutionService;
    private readonly ICharacterReactionExecutionService _reactionService;
    private readonly ISceneVisualStateReader _visualStateReader;
    private readonly ILogger<AutonomousCharacterLifecycleOrchestrator> _logger;

    public AutonomousCharacterLifecycleOrchestrator(
        ProjectDbContext dbContext,
        IAutonomousDecisionService decisionService,
        IActivityExecutionService activityExecutionService,
        ICharacterReactionExecutionService reactionService,
        ISceneVisualStateReader visualStateReader,
        ILogger<AutonomousCharacterLifecycleOrchestrator> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _decisionService = decisionService ?? throw new ArgumentNullException(nameof(decisionService));
        _activityExecutionService = activityExecutionService ?? throw new ArgumentNullException(nameof(activityExecutionService));
        _reactionService = reactionService ?? throw new ArgumentNullException(nameof(reactionService));
        _visualStateReader = visualStateReader ?? throw new ArgumentNullException(nameof(visualStateReader));
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
            status: AutonomyTickStatus.Running,
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
            _logger.LogInformation(
                "[AutonomousCharacterLifecycleOrchestrator] Duplicate tick suppressed by DB unique constraint for CharacterId={CharacterId}, TimeBucket={TimeBucket}, ExecutionId={ExecutionId}",
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

        ReactionExecutionResult? reactionResult = null;
        ActivityExecutionResult? activityResult = null;

        try
        {
            // 2. Load Character Context
            var character = await _dbContext.Characters.FindAsync(new object?[] { request.CharacterId }, ct);
            if (character == null)
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

            // 3. Load Authoritative Visual State and Location
            var latestVisualState = await _visualStateReader.GetLatestByCharacterIdAsync(character.Id, ct);
            var currentVisualState = latestVisualState?.CharacterState;
            var currentLocation = !string.IsNullOrWhiteSpace(latestVisualState?.Location)
                ? latestVisualState.Location
                : "Sanctuary";
            int sceneRevision = latestVisualState != null ? latestVisualState.SceneRevision : 1;

            var currentState = CharacterStateSnapshot.CreateDefault();

            // 4. Load Recent Activities, Visual Memories, and Goals
            var recentActivities = await _dbContext.CharacterActivities
                .AsNoTracking()
                .Where(a => a.CharacterId == character.Id)
                .OrderByDescending(a => a.CreatedAt)
                .Take(5)
                .ToListAsync(ct);

            var recentMemories = await _dbContext.CharacterVisualMemories
                .AsNoTracking()
                .Where(m => m.CharacterId == character.Id && m.ValidUntilTurnId == null)
                .OrderByDescending(m => m.CreatedAt)
                .Take(5)
                .ToListAsync(ct);

            var dbGoals = await _dbContext.CharacterGoals
                .AsNoTracking()
                .Include(g => g.Milestones)
                .Where(g => g.CharacterId == character.Id && g.Status == CharacterGoalStatus.Active)
                .OrderByDescending(g => g.Priority)
                .ThenByDescending(g => g.CreatedAt)
                .ToListAsync(ct);

            var goalSnapshots = dbGoals.Select(g =>
            {
                var activeM = g.Milestones.FirstOrDefault(m => m.Status == CharacterGoalMilestoneStatus.Active);
                float mProg = activeM != null && activeM.TargetValue > 0 ? (float)(activeM.CurrentValue / activeM.TargetValue) : 0f;
                return new CharacterGoalSnapshot(
                    GoalId: g.Id,
                    CharacterId: g.CharacterId,
                    Title: g.Title,
                    GoalType: g.GoalType,
                    Priority: g.Priority,
                    Status: g.Status,
                    Progress: g.Progress,
                    CurrentValue: g.CurrentValue,
                    TargetValue: g.TargetValue,
                    CurrentMilestone: activeM?.Title,
                    MilestoneProgress: mProg,
                    Description: g.Description
                );
            }).ToList();

            var goalSnapshotsForReaction = dbGoals.Select(g => new Domain.ValueObjects.GoalSnapshot(
                GoalId: g.Id,
                CharacterId: g.CharacterId,
                Title: g.Title,
                GoalType: g.GoalType,
                Priority: g.Priority,
                Status: g.Status,
                Progress: g.Progress,
                CurrentValue: g.CurrentValue,
                TargetValue: g.TargetValue
            )).ToList();

            IReadOnlyList<string>? activeGoals = null;
            if (goalSnapshots.Count > 0)
            {
                activeGoals = goalSnapshots.Select(g => g.Title).ToList();
            }
            else if (!string.IsNullOrWhiteSpace(character.CustomMilestonesJson))
            {
                try
                {
                    activeGoals = JsonSerializer.Deserialize<List<string>>(character.CustomMilestonesJson);
                }
                catch
                {
                    activeGoals = new[] { character.CustomMilestonesJson.Trim() };
                }
            }

            // 5. Evaluate World Event Reaction (if provided)
            if (request.WorldEventId.HasValue)
            {
                var worldEvent = await _dbContext.CharacterWorldEvents.FindAsync(new object?[] { request.WorldEventId.Value }, ct);
                if (worldEvent != null)
                {
                    var reactionRequest = new ReactionExecutionRequest(
                        WorldEvent: worldEvent,
                        Character: character,
                        ExecutionId: request.ExecutionId,
                        CurrentTime: now,
                        CurrentState: currentState,
                        CurrentVisualState: currentVisualState,
                        CurrentGoals: goalSnapshotsForReaction,
                        SceneRevision: sceneRevision
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

            // 6. Autonomous Decision
            var decisionRequest = new AutonomousDecisionRequest(
                CharacterId: character.Id,
                CurrentTime: now,
                CurrentLocation: currentLocation,
                TimeBucket: request.TimeBucket,
                CurrentVisualState: currentVisualState,
                StateSnapshot: currentState,
                RecentActivities: recentActivities,
                RecentVisualMemories: recentMemories,
                PersonalityPrompt: character.PersonalityPrompt,
                WorldDescription: character.WorldDescription,
                ActiveGoals: activeGoals,
                Goals: goalSnapshots,
                SceneRevision: sceneRevision
            );

            var decision = await _decisionService.DecideNextActionAsync(decisionRequest, ct);

            // 7. Execute Activity (if decided)
            if (decision.Action == AutonomousDecisionAction.PerformActivity && decision.Candidate != null)
            {
                var executionRequest = new ActivityExecutionRequest(
                    Character: character,
                    Candidate: decision.Candidate,
                    CurrentTime: now,
                    TimeBucket: request.TimeBucket,
                    ExecutionId: request.ExecutionId,
                    CurrentVisualState: currentVisualState,
                    CurrentState: currentState,
                    SceneRevision: sceneRevision
                );

                activityResult = await _activityExecutionService.ExecuteActivityAsync(executionRequest, ct);
            }

            // 8. Complete Tick Atomically
            tick.Complete(
                completedAt: DateTime.UtcNow,
                activityId: activityResult?.Activity?.Id,
                sceneSpecificationId: activityResult?.SceneSpecificationId,
                decisionFingerprint: decision?.Candidate?.DecisionFingerprint
            );

            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[AutonomousCharacterLifecycleOrchestrator] Successfully completed tick for CharacterId={CharacterId}, TimeBucket={TimeBucket}, ExecutionId={ExecutionId}, ActivityId={ActivityId}",
                character.Id, request.TimeBucket, request.ExecutionId, activityResult?.Activity?.Id);

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
