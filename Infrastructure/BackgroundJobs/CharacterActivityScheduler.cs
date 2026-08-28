using System.Text.Json;
using Application.Contracts.Activities;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.BackgroundJobs;

/// <summary>
/// Authoritative background scheduler for evaluating and scheduling autonomous character activities.
/// Distributed-safe, idempotent, and non-blocking with respect to the generation engine.
/// </summary>
public sealed class CharacterActivityScheduler
{
    private readonly ProjectDbContext _dbContext;
    private readonly ICharacterActivityDecisionService _decisionService;
    private readonly ISceneCompositionPipelineService _sceneCompositionPipeline;
    private readonly ISceneVisualStateReader _visualStateReader;
    private readonly ILogger<CharacterActivityScheduler> _logger;

    public CharacterActivityScheduler(
        ProjectDbContext dbContext,
        ICharacterActivityDecisionService decisionService,
        ISceneCompositionPipelineService sceneCompositionPipeline,
        ISceneVisualStateReader visualStateReader,
        ILogger<CharacterActivityScheduler> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _decisionService = decisionService ?? throw new ArgumentNullException(nameof(decisionService));
        _sceneCompositionPipeline = sceneCompositionPipeline ?? throw new ArgumentNullException(nameof(sceneCompositionPipeline));
        _visualStateReader = visualStateReader ?? throw new ArgumentNullException(nameof(visualStateReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        // Exclude characters who already have an autonomous activity recorded for this time bucket
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

        // Parse Active Goals from database / character milestones
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

        var decisionRequest = new CharacterActivityDecisionRequest(
            CharacterId: character.Id,
            CurrentTime: now,
            CurrentLocation: currentLocation,
            TimeBucket: timeBucket,
            CurrentVisualState: currentVisualState,
            RecentActivities: recentActivities,
            RecentVisualMemories: recentMemories,
            PersonalityPrompt: character.PersonalityPrompt,
            WorldDescription: character.WorldDescription,
            ActiveGoals: activeGoals,
            Goals: goalSnapshots,
            SceneRevision: sceneRevision
        );

        // 2. Decide next activity candidate
        var candidate = await _decisionService.DecideAsync(decisionRequest, ct);
        if (candidate == null)
        {
            _logger.LogWarning("[CharacterActivityScheduler] Decision service returned null for CharacterId={CharacterId}", character.Id);
            return false;
        }

        // 3. Create Activity Entity
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

        // 4. Persist with Distributed Concurrency Guard (Suppress ONLY unique constraint duplicates)
        try
        {
            await _dbContext.CharacterActivities.AddAsync(activity, ct);
            await _dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            if (IsUniqueConstraintViolation(ex))
            {
                _logger.LogInformation(ex, "[CharacterActivityScheduler] Idempotent duplicate suppressed for CharacterId={CharacterId}, TimeBucket={TimeBucket}",
                    character.Id, timeBucket);
                return false;
            }

            // Real database failure (connection loss, foreign key, NOT NULL, schema error) -> propagate!
            _logger.LogError(ex, "[CharacterActivityScheduler] Non-duplicate database failure saving activity for CharacterId={CharacterId}, TimeBucket={TimeBucket}",
                character.Id, timeBucket);
            throw;
        }

        // 5. If Visual Moment Accepted -> Map to SceneIntent and execute Scene Composition Pipeline
        if (candidate.ShouldCreateVisualMoment)
        {
            try
            {
                var sceneIntent = CharacterActivitySceneIntentMapper.MapToSceneIntent(
                    activity: activity,
                    candidate: candidate,
                    currentVisualState: currentVisualState,
                    sessionId: null,
                    turnId: null
                );

                activity.LinkSceneIntent(sceneIntent.Id);

                var pipelineResult = await _sceneCompositionPipeline.ExecuteAsync(
                    intent: sceneIntent,
                    generationProfile: GenerationProfile.CreateDefault(),
                    sceneRevision: sceneRevision,
                    ct: ct
                );

                if (pipelineResult?.SceneSpecification != null)
                {
                    await _dbContext.SceneSpecifications.AddAsync(pipelineResult.SceneSpecification, ct);
                    await _dbContext.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CharacterActivityScheduler] Visual moment composition failed for ActivityId={ActivityId}. Activity remains valid.", activity.Id);
                // Generation failure does NOT corrupt or cancel the activity
            }
        }

        return true;
    }

    public static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        while (inner != null)
        {
            // 1. Direct Typed Npgsql PostgresException Check
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
                if (errCode is int code && code == 19) return true;
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

    public static string GetTimeBucket(DateTime time)
    {
        return time.ToUniversalTime().ToString("yyyy-MM-ddTHH:00");
    }
}
