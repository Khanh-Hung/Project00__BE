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

public sealed class CharacterReactionMemoryIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public CharacterReactionMemoryIntegrationTests()
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
    public async Task RepeatedAmbientEvents_DoNotCreateMemorySpam()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };

        using (var db = new ProjectDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.SaveChangesAsync();
        }

        // 4 repeated ambient weather events
        for (int i = 1; i <= 4; i++)
        {
            var rainEvent = CharacterWorldEvent.Create(
                characterId: charId,
                eventType: CharacterWorldEventType.ExternalWorldEvent,
                sourceType: "Weather",
                payloadJson: $"Rain started falling lightly (tick {i})."
            );

            using (var db = new ProjectDbContext(_options))
            {
                await db.CharacterWorldEvents.AddAsync(rainEvent);
                await db.SaveChangesAsync();

                var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
                var fakePipeline = new FakeSceneCompositionPipelineService();
                var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
                var execService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
                var reactionService = new CharacterReactionExecutionService(db, goalService, execService, fakePipeline, stateReader, NullLogger<CharacterReactionExecutionService>.Instance);

                var request = new ReactionExecutionRequest(rainEvent, character, Guid.NewGuid(), DateTime.UtcNow);
                var result = await reactionService.ExecuteReactionAsync(request);

                Assert.True(result.Success);
                Assert.False(result.MemoryCreated);
                Assert.Null(result.MemoryId);
            }
        }

        // Invariant: Zero memory rows created after 4 ambient ticks
        using (var db = new ProjectDbContext(_options))
        {
            var memCount = await db.CharacterMemories.CountAsync(m => m.CharacterId == charId);
            Assert.Equal(0, memCount);
        }
    }

    [Fact]
    public async Task MeaningfulLifeEvent_CreatesMemoryCandidate_AndRetryDoesNotDuplicate()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };
        var loveEvent = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.UserMessage,
            sourceType: "Chat",
            payloadJson: "I love your artwork and cherish our friendship forever!"
        );

        using (var db = new ProjectDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterWorldEvents.AddAsync(loveEvent);
            await db.SaveChangesAsync();
        }

        var executionId = Guid.NewGuid();
        var request = new ReactionExecutionRequest(loveEvent, character, executionId, DateTime.UtcNow);

        // Attempt 1: First invocation -> Memory created
        using (var db = new ProjectDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(db, goalService, execService, fakePipeline, stateReader, NullLogger<CharacterReactionExecutionService>.Instance);

            var res1 = await reactionService.ExecuteReactionAsync(request);
            Assert.True(res1.Success);
            Assert.True(res1.MemoryCreated);
            Assert.NotNull(res1.MemoryId);
        }

        // Attempt 2: Sequential retry -> Duplicate suppressed, zero new memory created
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
            Assert.False(res2.MemoryCreated);
        }

        // Invariant: Exactly 1 row in CharacterMemories
        using (var db = new ProjectDbContext(_options))
        {
            var memCount = await db.CharacterMemories.CountAsync(m => m.CharacterId == charId);
            Assert.Equal(1, memCount);
        }
    }

    [Fact]
    public async Task MultipleDistinctMeaningfulEvents_CreateSeparateMemories()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };

        var event1 = CharacterWorldEvent.Create(charId, CharacterWorldEventType.UserMessage, "Chat", payloadJson: "Congratulations on completing your alchemy masterpiece!");
        var event2 = CharacterWorldEvent.Create(charId, CharacterWorldEventType.GoalCompleted, "Goal", payloadJson: "Master Alchemist title unlocked!");

        using (var db = new ProjectDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterWorldEvents.AddRangeAsync(event1, event2);
            await db.SaveChangesAsync();
        }

        using (var db = new ProjectDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(db, goalService, execService, fakePipeline, stateReader, NullLogger<CharacterReactionExecutionService>.Instance);

            var res1 = await reactionService.ExecuteReactionAsync(new ReactionExecutionRequest(event1, character, Guid.NewGuid(), DateTime.UtcNow));
            var res2 = await reactionService.ExecuteReactionAsync(new ReactionExecutionRequest(event2, character, Guid.NewGuid(), DateTime.UtcNow));

            Assert.True(res1.Success && res1.MemoryCreated);
            Assert.True(res2.Success && res2.MemoryCreated);
        }

        using (var db = new ProjectDbContext(_options))
        {
            var memCount = await db.CharacterMemories.CountAsync(m => m.CharacterId == charId);
            Assert.Equal(2, memCount);
        }
    }
}
