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
    private readonly DbContextOptions<ProjectDbContext> _options;

    public CharacterReactionConcurrencyTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new ProjectDbContext(_options);
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

        using (var db = new ProjectDbContext(_options))
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
            await using var workerDb = new ProjectDbContext(_options);
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
    public async Task TenConcurrentWorkers_ProcessingSameWorldEvent_AllowsExactlyOneWinnerAndNineSuppressed()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };
        var worldEvent = CharacterWorldEvent.Create(charId, CharacterWorldEventType.UserMessage, "Chat", payloadJson: "Your new artwork is wonderful!");

        using (var db = new ProjectDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterWorldEvents.AddAsync(worldEvent);
            await db.SaveChangesAsync();
        }

        var sharedExecutionId = Guid.NewGuid();
        var request = new ReactionExecutionRequest(
            WorldEvent: worldEvent,
            Character: character,
            ExecutionId: sharedExecutionId,
            CurrentTime: DateTime.UtcNow,
            CurrentState: CharacterStateSnapshot.CreateDefault()
        );

        var tasks = Enumerable.Range(1, 10).Select(async _ =>
        {
            await using var workerDb = new ProjectDbContext(_options);
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

        // Assert Database Invariants: Exactly 1 row in CharacterWorldEventReactions and 1 Memory
        using (var db = new ProjectDbContext(_options))
        {
            var count = await db.CharacterWorldEventReactions.CountAsync(r => r.CharacterId == charId && r.WorldEventId == worldEvent.Id);
            Assert.Equal(1, count);

            var memCount = await db.CharacterMemories.CountAsync(m => m.CharacterId == charId);
            Assert.Equal(1, memCount);
        }
    }
}
