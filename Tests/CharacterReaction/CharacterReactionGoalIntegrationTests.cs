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

public sealed class CharacterReactionGoalIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public CharacterReactionGoalIntegrationTests()
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
    public async Task PositiveReaction_WithActiveGoal_RecordsGoalProgress()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Painter", "http://avatar.png", "Painter", "Hello", "Anime") { Id = charId };
        var goal = new CharacterGoal(charId, "Master Landscape Art", CharacterGoalType.SkillDevelopment, 100);
        var worldEvent = CharacterWorldEvent.Create(charId, CharacterWorldEventType.UserMessage, "Chat", payloadJson: "Your painting exhibition was a grand success!");

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

        var request = new ReactionExecutionRequest(
            WorldEvent: worldEvent,
            Character: character,
            ExecutionId: Guid.NewGuid(),
            CurrentTime: DateTime.UtcNow,
            CurrentState: CharacterStateSnapshot.CreateDefault(),
            CurrentGoals: new[] { goalSnapshot }
        );

        using (var db = new CoreDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, new Infrastructure.Services.State.CharacterStateTransitionService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Services.State.CharacterStateTransitionService>.Instance), Microsoft.Extensions.Logging.Abstractions.NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(db, goalService, execService, fakePipeline, stateReader, new Infrastructure.Services.State.CharacterStateTransitionService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Services.State.CharacterStateTransitionService>.Instance), Microsoft.Extensions.Logging.Abstractions.NullLogger<CharacterReactionExecutionService>.Instance);

            var result = await reactionService.ExecuteReactionAsync(request);

            Assert.True(result.Success);
            Assert.True(result.GoalContributed);
            Assert.Equal(goal.Id, result.GoalId);
        }

        using (var db = new CoreDbContext(_options))
        {
            var updatedGoal = await db.CharacterGoals.FirstAsync(g => g.Id == goal.Id);
            Assert.True(updatedGoal.CurrentValue > 0);
        }
    }
}
