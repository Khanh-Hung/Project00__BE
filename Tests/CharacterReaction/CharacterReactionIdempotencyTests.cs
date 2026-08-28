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

public sealed class CharacterReactionIdempotencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public CharacterReactionIdempotencyTests()
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
    public async Task SequentialRetry_WithSameExecutionId_ReturnsDuplicateSuppressed_WithoutDuplicateEffects()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };
        var worldEvent = CharacterWorldEvent.Create(charId, CharacterWorldEventType.UserMessage, "Chat", payloadJson: "Great painting!");

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

        // Attempt 1: First invocation -> Success
        using (var db = new ProjectDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(db, goalService, execService, fakePipeline, stateReader, NullLogger<CharacterReactionExecutionService>.Instance);

            var res1 = await reactionService.ExecuteReactionAsync(request);

            Assert.True(res1.Success);
            Assert.False(res1.IsDuplicateSuppressed);
            Assert.Equal(executionId, res1.ExecutionId);
            Assert.NotNull(res1.Reaction);
        }

        // Attempt 2: Sequential Retry with same ExecutionId -> Duplicate Suppressed
        using (var db = new ProjectDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(db, goalService, execService, fakePipeline, stateReader, NullLogger<CharacterReactionExecutionService>.Instance);

            var res2 = await reactionService.ExecuteReactionAsync(request);

            Assert.True(res2.Success);
            Assert.True(res2.IsDuplicateSuppressed);
            Assert.Equal(executionId, res2.ExecutionId);
            Assert.Null(res2.Reaction);
        }

        // Invariant check: Exactly 1 row in CharacterWorldEventReactions and 1 Memory
        using (var db = new ProjectDbContext(_options))
        {
            var count = await db.CharacterWorldEventReactions.CountAsync(r => r.CharacterId == charId && r.WorldEventId == worldEvent.Id);
            Assert.Equal(1, count);

            var memCount = await db.CharacterMemories.CountAsync(m => m.CharacterId == charId);
            Assert.Equal(1, memCount);
        }
    }
}
