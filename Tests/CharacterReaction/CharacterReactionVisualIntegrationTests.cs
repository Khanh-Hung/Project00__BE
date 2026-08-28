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

public sealed class CharacterReactionVisualIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public CharacterReactionVisualIntegrationTests()
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
    public async Task JoyousReaction_TriggersVisualMomentAndCreatesSceneSpecification()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };
        var worldEvent = CharacterWorldEvent.Create(charId, CharacterWorldEventType.UserMessage, "Chat", payloadJson: "I love your work, thank you!");

        using (var db = new CoreDbContext(_options))
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
            CurrentState: CharacterStateSnapshot.CreateDefault(),
            CurrentVisualState: new CharacterVisualState(charId, "Art Studio", sceneRevision: 1)
        );

        using (var db = new CoreDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(db, goalService, execService, fakePipeline, stateReader, NullLogger<CharacterReactionExecutionService>.Instance);

            var result = await reactionService.ExecuteReactionAsync(request);

            Assert.True(result.Success);
            Assert.True(result.VisualMomentCreated);
            Assert.NotNull(result.SceneSpecificationId);
        }

        using (var db = new CoreDbContext(_options))
        {
            var spec = await db.SceneSpecifications.FirstOrDefaultAsync(s => s.CharacterId == charId);
            Assert.NotNull(spec);
        }
    }
}
