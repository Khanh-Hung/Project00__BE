using Application.Contracts.Reactions;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services.Autonomous;
using Infrastructure.Services.Goals;
using Infrastructure.Services.Reactions;
using Infrastructure.Services.Scene;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Helpers;
using Xunit;

namespace Tests.CharacterReaction;

public sealed class CharacterReactionConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public CharacterReactionConcurrencyTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new CoreDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task TwoConcurrentWorkers_ProcessingSameWorldEvent_AllowsOneWinnerAndOneSuppression()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };
        var worldEvent = CharacterWorldEvent.Create(charId, CharacterWorldEventType.UserMessage, "Chat", payloadJson: "Great work!");

        using (var db = new CoreDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterWorldEvents.AddAsync(worldEvent);
            await db.SaveChangesAsync();
        }

        var executionId = Guid.NewGuid();
        var request = new ReactionExecutionRequest(
            WorldEvent: worldEvent,
            Character: character,
            ExecutionId: executionId,
            CurrentTime: DateTime.UtcNow,
            CurrentState: CharacterStateSnapshot.CreateDefault()
        );

        var tasks = Enumerable.Range(1, 2).Select(async _ =>
        {
            await using var workerDb = new CoreDbContext(_options);
            var goalService = new GoalProgressService(workerDb, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(workerDb, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(workerDb, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(workerDb, goalService, execService, fakePipeline, stateReader, NullLogger<CharacterReactionExecutionService>.Instance);

            return await reactionService.ExecuteReactionAsync(request);
        }).ToList();

        var results = await Task.WhenAll(tasks);

        int winners = results.Count(r => r.Success && !r.IsDuplicateSuppressed);
        int suppressed = results.Count(r => r.Success && r.IsDuplicateSuppressed);

        Assert.Equal(1, winners);
        Assert.Equal(1, suppressed);
    }

    [Fact]
    public async Task TenConcurrentWorkers_ProcessingSameWorldEvent_AllowsExactlyOneWinnerAndNineSuppressed_WithZeroDuplicateSideEffects()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };
        var goal = new CharacterGoal(charId, "Master Landscape Art", CharacterGoalType.SkillDevelopment, 100);
        var worldEvent = CharacterWorldEvent.Create(charId, CharacterWorldEventType.UserMessage, "Chat", payloadJson: "Your new artwork exhibition is magnificent!");

        using (var db = new CoreDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterGoals.AddAsync(goal);
            await db.CharacterWorldEvents.AddAsync(worldEvent);
            await db.SaveChangesAsync();
        }

        var goalSnapshot = new GoalSnapshot(
            GoalId: goal.Id,
            CharacterId: charId,
            Title: goal.Title,
            GoalType: goal.GoalType,
            Priority: goal.Priority,
            Status: goal.Status,
            Progress: goal.Progress,
            CurrentValue: goal.CurrentValue,
            TargetValue: goal.TargetValue
        );

        var sharedExecutionId = Guid.NewGuid();
        var request = new ReactionExecutionRequest(
            WorldEvent: worldEvent,
            Character: character,
            ExecutionId: sharedExecutionId,
            CurrentTime: DateTime.UtcNow,
            CurrentState: CharacterStateSnapshot.CreateDefault(),
            CurrentVisualState: new CharacterVisualState(charId, "Art Studio", sceneRevision: 1),
            CurrentGoals: new[] { goalSnapshot }
        );

        var tasks = Enumerable.Range(1, 10).Select(async _ =>
        {
            await using var workerDb = new CoreDbContext(_options);
            var goalService = new GoalProgressService(workerDb, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(workerDb, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(workerDb, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(workerDb, goalService, execService, fakePipeline, stateReader, NullLogger<CharacterReactionExecutionService>.Instance);

            return await reactionService.ExecuteReactionAsync(request);
        }).ToList();

        var results = await Task.WhenAll(tasks);

        int winners = results.Count(r => r.Success && !r.IsDuplicateSuppressed);
        int suppressed = results.Count(r => r.Success && r.IsDuplicateSuppressed);

        Assert.Equal(1, winners);
        Assert.Equal(9, suppressed);

        // Assert Strict Database Invariants:
        // 1. Exactly 1 row in CharacterWorldEventReactions
        // 2. Exactly 1 row in CharacterMemories
        // 3. Exactly 1 Goal contribution applied (CurrentValue > 0, not multiplied by 10)
        // 4. Exactly 1 SceneSpecification generated
        using (var db = new CoreDbContext(_options))
        {
            var count = await db.CharacterWorldEventReactions.CountAsync(r => r.CharacterId == charId && r.WorldEventId == worldEvent.Id);
            Assert.Equal(1, count);

            var memCount = await db.CharacterMemories.CountAsync(m => m.CharacterId == charId);
            Assert.Equal(1, memCount);

            var dbGoal = await db.CharacterGoals.FirstAsync(g => g.Id == goal.Id);
            Assert.Equal(2.0, dbGoal.CurrentValue); // 2.0 single contribution, NOT 20.0

            var specCount = await db.SceneSpecifications.CountAsync(s => s.CharacterId == charId);
            Assert.Equal(1, specCount);
        }
    }
}
