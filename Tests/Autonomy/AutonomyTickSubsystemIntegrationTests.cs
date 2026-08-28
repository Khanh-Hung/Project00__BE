using Application.Contracts.Autonomy;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Services.Autonomous;
using Infrastructure.Services.Autonomy;
using Infrastructure.Services.Goals;
using Infrastructure.Services.Reactions;
using Infrastructure.Services.Scene;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Helpers;
using Xunit;

namespace Tests.Autonomy;

public sealed class AutonomyTickSubsystemIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public AutonomyTickSubsystemIntegrationTests()
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
    public async Task Orchestrator_ReusesExistingSubsystems_WithoutDirectComfyUICalls()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Scholar and Arcane Researcher", "http://avatar.png", "Scholar", "Hello", "Anime") { Id = charId };
        var goal = new CharacterGoal(charId, "Master Alchemical Research", CharacterGoalType.SkillDevelopment, 100);
        var worldEvent = CharacterWorldEvent.Create(charId, CharacterWorldEventType.UserMessage, "Chat", payloadJson: "Great alchemical research breakthrough!");

        using (var db = new ProjectDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterGoals.AddAsync(goal);
            await db.CharacterWorldEvents.AddAsync(worldEvent);
            await db.SaveChangesAsync();
        }

        using (var db = new ProjectDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService(); // Authoritative scene pipeline stub, no direct ComfyUI
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var contextLoader = new AutonomousCharacterContextLoader(db, stateReader, NullLogger<AutonomousCharacterContextLoader>.Instance);
            var decisionService = new AutonomousDecisionService(NullLogger<AutonomousDecisionService>.Instance);
            var activityExecService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, NullLogger<ActivityExecutionService>.Instance);
            var reactionService = new CharacterReactionExecutionService(db, goalService, activityExecService, fakePipeline, stateReader, NullLogger<CharacterReactionExecutionService>.Instance);

            var orchestrator = new AutonomousCharacterLifecycleOrchestrator(
                db,
                contextLoader,
                decisionService,
                activityExecService,
                reactionService,
                NullLogger<AutonomousCharacterLifecycleOrchestrator>.Instance
            );

            var request = new AutonomyTickRequest(
                CharacterId: charId,
                ExecutionId: Guid.NewGuid(),
                TimeBucket: "2026-08-28T14:00",
                CurrentTime: new DateTime(2026, 8, 28, 14, 0, 0, DateTimeKind.Utc),
                WorldEventId: worldEvent.Id
            );

            var result = await orchestrator.ExecuteTickAsync(request);

            Assert.True(result.Success);
            Assert.NotNull(result.Tick);
            Assert.NotNull(result.ActivityResult);
            Assert.True(result.ActivityResult.Success);

            // Verify Goal was progressed through GoalProgressService: 30.0 (Reaction) + 2.0 (Activity) = 32.0
            var dbGoal = await db.CharacterGoals.FirstAsync(g => g.Id == goal.Id);
            Assert.Equal(32.0, dbGoal.CurrentValue); // Exact 32.0 contribution!

            // Verify SceneSpecification was persisted via ISceneCompositionPipelineService
            var spec = await db.SceneSpecifications.FirstOrDefaultAsync(s => s.CharacterId == charId);
            Assert.NotNull(spec);
        }
    }
}
