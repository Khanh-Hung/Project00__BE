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
    private readonly ILogger<CharacterActivityScheduler> _logger;

    public CharacterActivityScheduler(
        ProjectDbContext dbContext,
        ICharacterActivityDecisionService decisionService,
        ISceneCompositionPipelineService sceneCompositionPipeline,
        ILogger<CharacterActivityScheduler> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _decisionService = decisionService ?? throw new ArgumentNullException(nameof(decisionService));
        _sceneCompositionPipeline = sceneCompositionPipeline ?? throw new ArgumentNullException(nameof(sceneCompositionPipeline));
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
        // 1. Fetch character context for decision
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

        var currentLocation = character.WorldDescription ?? "Sanctuary";

        var decisionRequest = new CharacterActivityDecisionRequest(
            CharacterId: character.Id,
            CurrentTime: now,
            CurrentLocation: currentLocation,
            TimeBucket: timeBucket,
            CurrentVisualState: null,
            RecentActivities: recentActivities,
            RecentVisualMemories: recentMemories,
            PersonalityPrompt: character.PersonalityPrompt,
            WorldDescription: character.WorldDescription,
            ActiveGoals: null,
            SceneRevision: 1
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
            status: CharacterActivityStatus.Started,
            now: now
        );

        // 4. Persist with Distributed Concurrency Guard
        try
        {
            await _dbContext.CharacterActivities.AddAsync(activity, ct);
            await _dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogInformation(ex, "[CharacterActivityScheduler] Idempotent duplicate suppressed for CharacterId={CharacterId}, TimeBucket={TimeBucket}",
                character.Id, timeBucket);
            return false;
        }

        // 5. If Visual Moment Accepted -> Map to SceneIntent and execute Scene Composition Pipeline
        if (candidate.ShouldCreateVisualMoment)
        {
            try
            {
                var sceneIntent = CharacterActivitySceneIntentMapper.MapToSceneIntent(
                    activity: activity,
                    candidate: candidate,
                    currentVisualState: null,
                    sessionId: null,
                    turnId: activity.Id
                );

                activity.LinkSceneIntent(activity.Id);

                var pipelineResult = await _sceneCompositionPipeline.ExecuteAsync(
                    intent: sceneIntent,
                    generationProfile: GenerationProfile.CreateDefault(),
                    sceneRevision: 1,
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

    public static string GetTimeBucket(DateTime time)
    {
        return time.ToUniversalTime().ToString("yyyy-MM-ddTHH:00");
    }
}
