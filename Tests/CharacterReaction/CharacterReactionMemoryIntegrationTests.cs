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
    public async Task SignificantEvent_CreatesCharacterMemoryRecord()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };
        var worldEvent = CharacterWorldEvent.Create(charId, CharacterWorldEventType.UserMessage, "Chat", payloadJson: "Congratulations on completing your alchemy masterpiece!");

        using (var db = new ProjectDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterWorldEvents.AddAsync(worldEvent);
            await db.SaveChangesAsync();
        }

        var request = new ReactionExecutionRequest(
            WorldEvent: worldEvent,
            Character: character,
            ExecutionId: Guid.NewGuid(),
            CurrentTime: DateTime.UtcNow,
            CurrentState: CharacterStateSnapshot.CreateDefault()
        );

        using (var db = new ProjectDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(db, goalService, execService, fakePipeline, stateReader, NullLogger<CharacterReactionExecutionService>.Instance);

            var result = await reactionService.ExecuteReactionAsync(request);

            Assert.True(result.Success);
            Assert.True(result.MemoryCreated);
            Assert.NotNull(result.MemoryId);
        }

        using (var db = new ProjectDbContext(_options))
        {
            var memory = await db.CharacterMemories.FirstOrDefaultAsync(m => m.CharacterId == charId);
            Assert.NotNull(memory);
            Assert.Equal(MemoryType.Event, memory.Type);
        }
    }

    [Fact]
    public async Task LowValueSystemNotice_DoesNotCreateMemoryRecord_SpamPrevention()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };
        var worldEvent = CharacterWorldEvent.Create(charId, CharacterWorldEventType.SystemEvent, "System", payloadJson: "Routine ping");

        using (var db = new ProjectDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterWorldEvents.AddAsync(worldEvent);
            await db.SaveChangesAsync();
        }

        var request = new ReactionExecutionRequest(
            WorldEvent: worldEvent,
            Character: character,
            ExecutionId: Guid.NewGuid(),
            CurrentTime: DateTime.UtcNow,
            CurrentState: CharacterStateSnapshot.CreateDefault()
        );

        using (var db = new ProjectDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(db, goalService, execService, fakePipeline, stateReader, NullLogger<CharacterReactionExecutionService>.Instance);

            var result = await reactionService.ExecuteReactionAsync(request);

            Assert.True(result.Success);
            Assert.False(result.MemoryCreated);
            Assert.Null(result.MemoryId);
        }

        using (var db = new ProjectDbContext(_options))
        {
            var memCount = await db.CharacterMemories.CountAsync(m => m.CharacterId == charId);
            Assert.Equal(0, memCount);
        }
    }
}
