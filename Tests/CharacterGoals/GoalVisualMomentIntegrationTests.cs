using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.BackgroundJobs;
using Infrastructure.Persistence;
using Infrastructure.Services.Scene;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Helpers;
using Xunit;

namespace Tests.CharacterGoals;

public sealed class GoalVisualMomentIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public GoalVisualMomentIntegrationTests()
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
    public async Task GoalDrivenVisualMoment_ExecutesSceneCompositionPipeline_WithoutInvokingImageGeneration()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Seraphina", "Princess", "http://avatar.png", "noble explorer", "Hello", "Anime")
        {
            Id = charId
        };

        var goal = new CharacterGoal(charId, "Explore Ancient Sunken Ruins", CharacterGoalType.Exploration, 100, CharacterGoalPriority.Critical);

        using (var db = new CoreDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterGoals.AddAsync(goal);
            await db.SaveChangesAsync();
        }

        var decisionService = new CharacterActivityDecisionService(NullLogger<CharacterActivityDecisionService>.Instance);
        var fakePipeline = new FakeSceneCompositionPipelineService();

        var morningTime = new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc);
        var timeBucket = CharacterActivityScheduler.GetTimeBucket(morningTime);

        using (var db = new CoreDbContext(_options))
        {
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var scheduler = new CharacterActivityScheduler(
                db, decisionService, fakePipeline, stateReader, NullLogger<CharacterActivityScheduler>.Instance);

            var success = await scheduler.ProcessCharacterAsync(character, morningTime, timeBucket);
            Assert.True(success);
        }

        // Verify SceneSpecification was created and persisted
        using (var db = new CoreDbContext(_options))
        {
            var spec = await db.SceneSpecifications.FirstOrDefaultAsync(s => s.CharacterId == charId);
            Assert.NotNull(spec);
            Assert.NotNull(spec.SceneFingerprint);

            var activity = await db.CharacterActivities.FirstOrDefaultAsync(a => a.CharacterId == charId);
            Assert.NotNull(activity);
            Assert.True(activity.ShouldCreateVisualMoment);
            Assert.Equal(goal.Id, activity.GoalId);
        }
    }
}
