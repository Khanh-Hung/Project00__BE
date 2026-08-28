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

public sealed class CharacterReactionIdentityInvariantTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public CharacterReactionIdentityInvariantTests()
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
    public void IdentitySemantics_ExecutionId_WorldEventId_CorrelationId_AreDistinctConcepts()
    {
        var charId = Guid.NewGuid();
        var worldEventId = Guid.CreateVersion7();
        var executionId = Guid.CreateVersion7();
        var correlationId = $"corr-{Guid.NewGuid():N}";
        var decisionFingerprint = $"reaction-{worldEventId}";

        var worldEvent = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.UserMessage,
            sourceType: "Chat",
            payloadJson: "Hello Valerius!",
            correlationId: correlationId,
            id: worldEventId
        );

        Assert.Equal(worldEventId, worldEvent.Id);
        Assert.Equal(correlationId, worldEvent.CorrelationId);
        Assert.NotEqual(worldEvent.Id, executionId);
        Assert.NotEqual(worldEvent.CorrelationId, decisionFingerprint);
        Assert.NotEqual(executionId.ToString(), worldEvent.CorrelationId);
    }

    [Fact]
    public async Task DatabaseAuthoritativeIdempotency_ReliesOnWorldEventIdAndCharacterId_NotCallerExecutionId()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };
        var worldEvent = CharacterWorldEvent.Create(charId, CharacterWorldEventType.UserMessage, "Chat", payloadJson: "Great painting!");

        using (var db = new CoreDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterWorldEvents.AddAsync(worldEvent);
            await db.SaveChangesAsync();
        }

        var executionId1 = Guid.NewGuid();
        var executionId2 = Guid.NewGuid(); // Caller generates DIFFERENT ExecutionIds for same event attempt

        // First attempt with executionId1 -> Success
        using (var db = new CoreDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(db, goalService, execService, fakePipeline, stateReader, NullLogger<CharacterReactionExecutionService>.Instance);

            var req1 = new ReactionExecutionRequest(worldEvent, character, executionId1, DateTime.UtcNow);
            var res1 = await reactionService.ExecuteReactionAsync(req1);

            Assert.True(res1.Success);
            Assert.False(res1.IsDuplicateSuppressed);
        }

        // Second attempt with different executionId2 on SAME WorldEvent + CharacterId -> Database Unique Constraint catches duplicate!
        using (var db = new CoreDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(db, goalService, execService, fakePipeline, stateReader, NullLogger<CharacterReactionExecutionService>.Instance);

            var req2 = new ReactionExecutionRequest(worldEvent, character, executionId2, DateTime.UtcNow);
            var res2 = await reactionService.ExecuteReactionAsync(req2);

            Assert.True(res2.Success);
            Assert.True(res2.IsDuplicateSuppressed); // Correctly suppressed by (WorldEventId, CharacterId) DB unique index!
        }
    }
}
