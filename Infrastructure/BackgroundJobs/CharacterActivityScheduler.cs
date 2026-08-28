using System.Text.Json;
using Application.Contracts.Activities;
using Application.Contracts.Autonomous;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.BackgroundJobs;

/// <summary>
/// Authoritative background scheduler for waking and dispatching autonomous characters.
/// Dispatches to IAutonomousDecisionService and IActivityExecutionService.
/// Distributed-safe, idempotent, and non-blocking.
/// </summary>
public sealed class CharacterActivityScheduler
{
    private readonly ProjectDbContext _dbContext;
    private readonly IAutonomousDecisionService _decisionService;
    private readonly IActivityExecutionService _executionService;
    private readonly ISceneVisualStateReader _visualStateReader;
    private readonly ILogger<CharacterActivityScheduler> _logger;

    public CharacterActivityScheduler(
        ProjectDbContext dbContext,
        IAutonomousDecisionService decisionService,
        IActivityExecutionService executionService,
        ISceneVisualStateReader visualStateReader,
        ILogger<CharacterActivityScheduler> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _decisionService = decisionService ?? throw new ArgumentNullException(nameof(decisionService));
        _executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
        _visualStateReader = visualStateReader ?? throw new ArgumentNullException(nameof(visualStateReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public CharacterActivityScheduler(
        ProjectDbContext dbContext,
        ICharacterActivityDecisionService decisionService,
        ISceneCompositionPipelineService sceneCompositionPipeline,
        ISceneVisualStateReader visualStateReader,
        ILogger<CharacterActivityScheduler> logger)
        : this(
            dbContext,
            new Application.Services.AutonomousDecisionService(NullLogger<Application.Services.AutonomousDecisionService>.Instance),
            new Infrastructure.Services.Autonomous.ActivityExecutionService(
                dbContext,
                new Infrastructure.Services.Goals.GoalProgressService(dbContext, NullLogger<Infrastructure.Services.Goals.GoalProgressService>.Instance),
                sceneCompositionPipeline,
                visualStateReader,
                NullLogger<Infrastructure.Services.Autonomous.ActivityExecutionService>.Instance),
            visualStateReader,
            logger)
    {
    }

    public async Task<int> ExecuteCycleAsync(
        DateTime? currentTime = null,
        int batchSize = 10,
        CancellationToken ct = default)
    {
        var now = currentTime ?? DateTime.UtcNow;
        var timeBucket = GetTimeBucket(now);

        _logger.LogInformation("[CharacterActivityScheduler] Starting activity cycle for TimeBucket={TimeBucket}", timeBucket);

        // 1. Query Eligible Characters
        var existingClaimCharIds = await _dbContext.CharacterActivities
            .AsNoTracking()
            .Where(a => a.TimeBucket == timeBucket && a.Source == CharacterActivitySource.Autonomous)
            .Select(a => a.CharacterId)
            .ToListAsync(ct);

        var eligibleCharacters = await _dbContext.Characters
            .AsNoTracking()
            .Where(c => !c.IsSoftDeleted && !existingClaimCharIds.Contains(c.Id))
            .Take(batchSize)
            .ToListAsync(ct);

        int processedCount = 0;

        foreach (var character in eligibleCharacters)
        {
            try
            {
                var success = await ProcessCharacterAsync(character, now, timeBucket, ct);
                if (success)
                {
                    processedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CharacterActivityScheduler] Failed processing character {CharacterId}. Continuing batch.", character.Id);
            }
        }

        _logger.LogInformation("[CharacterActivityScheduler] Completed activity cycle. Processed {Count} characters.", processedCount);
        return processedCount;
    }

    public async Task<bool> ProcessCharacterAsync(
        Character character,
        DateTime now,
        string timeBucket,
        CancellationToken ct = default)
    {
        // 1. Fetch character context, authoritative visual state, and memory
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

        // Authoritative current visual state & location resolution
        var latestVisualState = await _visualStateReader.GetLatestByCharacterIdAsync(character.Id, ct);
        var currentVisualState = latestVisualState?.CharacterState;
        var currentLocation = !string.IsNullOrWhiteSpace(latestVisualState?.Location) 
            ? latestVisualState.Location 
            : "Sanctuary";
        int sceneRevision = latestVisualState != null ? latestVisualState.SceneRevision : 1;

        // Parse Active Goals from database
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
            return new Application.Contracts.Goals.CharacterGoalSnapshot(
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

        var decisionRequest = new AutonomousDecisionRequest(
            CharacterId: character.Id,
            CurrentTime: now,
            CurrentLocation: currentLocation,
            TimeBucket: timeBucket,
            CurrentVisualState: currentVisualState,
            StateSnapshot: CharacterStateSnapshot.CreateDefault(),
            RecentActivities: recentActivities,
            RecentVisualMemories: recentMemories,
            PersonalityPrompt: character.PersonalityPrompt,
            WorldDescription: character.WorldDescription,
            ActiveGoals: activeGoals,
            Goals: goalSnapshots,
            SceneRevision: sceneRevision
        );

        // 2. Decide next action via AutonomousDecisionService
        var decision = await _decisionService.DecideNextActionAsync(decisionRequest, ct);
        if (decision.Action == AutonomousDecisionAction.DoNothing || decision.Candidate == null)
        {
            _logger.LogInformation("[CharacterActivityScheduler] AutonomousDecisionService decided DoNothing for CharacterId={CharacterId}", character.Id);
            return false;
        }

        // 3. Execute Activity via ActivityExecutionService
        var executionRequest = new ActivityExecutionRequest(
            Character: character,
            Candidate: decision.Candidate,
            CurrentTime: now,
            TimeBucket: timeBucket,
            CurrentVisualState: currentVisualState,
            CurrentState: CharacterStateSnapshot.CreateDefault(),
            SceneRevision: sceneRevision
        );

        var executionResult = await _executionService.ExecuteActivityAsync(executionRequest, ct);
        return executionResult.Success && !executionResult.IsDuplicateSuppressed;
    }

    public static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        return Infrastructure.Services.Autonomous.ActivityExecutionService.IsUniqueConstraintViolation(ex);
    }

    public static string GetTimeBucket(DateTime time)
    {
        return time.ToUniversalTime().ToString("yyyy-MM-ddTHH:00");
    }
}
