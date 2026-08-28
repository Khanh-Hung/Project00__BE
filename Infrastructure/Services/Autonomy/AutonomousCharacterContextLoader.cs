using System.Text.Json;
using Application.Contracts.Autonomy;
using Application.Contracts.Goals;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Autonomy;

/// <summary>
/// Authoritative context loader for autonomous character ticks.
/// Queries and snapshots state, goals, recent memories, and visual context.
/// </summary>
public sealed class AutonomousCharacterContextLoader : IAutonomousCharacterContextLoader
{
    private readonly ProjectDbContext _dbContext;
    private readonly ISceneVisualStateReader _visualStateReader;
    private readonly ILogger<AutonomousCharacterContextLoader> _logger;

    public AutonomousCharacterContextLoader(
        ProjectDbContext dbContext,
        ISceneVisualStateReader visualStateReader,
        ILogger<AutonomousCharacterContextLoader> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _visualStateReader = visualStateReader ?? throw new ArgumentNullException(nameof(visualStateReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AutonomousCharacterContext?> LoadContextAsync(
        Guid characterId,
        DateTime currentTime,
        CancellationToken ct = default)
    {
        var character = await _dbContext.Characters.FindAsync(new object?[] { characterId }, ct);
        if (character == null)
        {
            _logger.LogWarning("[AutonomousCharacterContextLoader] Character {CharacterId} not found.", characterId);
            return null;
        }

        // 1. Authoritative Visual State & Location
        var latestVisualState = await _visualStateReader.GetLatestByCharacterIdAsync(character.Id, ct);
        var currentVisualState = latestVisualState?.CharacterState;
        var currentLocation = !string.IsNullOrWhiteSpace(latestVisualState?.Location)
            ? latestVisualState.Location
            : "Sanctuary";
        int sceneRevision = latestVisualState != null ? latestVisualState.SceneRevision : 1;

        var currentState = CharacterStateSnapshot.CreateDefault();

        // 2. Recent Activities & Visual Memories
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

        // 3. Active Goals
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

        var goalSnapshotsForReaction = dbGoals.Select(g => new GoalSnapshot(
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

        return new AutonomousCharacterContext(
            Character: character,
            CurrentVisualState: currentVisualState,
            CurrentLocation: currentLocation,
            SceneRevision: sceneRevision,
            CurrentState: currentState,
            RecentActivities: recentActivities,
            RecentVisualMemories: recentMemories,
            GoalSnapshots: goalSnapshots,
            GoalSnapshotsForReaction: goalSnapshotsForReaction,
            ActiveGoals: activeGoals
        );
    }
}
