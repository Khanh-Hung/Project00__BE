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

public sealed class GoalActivityIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public GoalActivityIntegrationTests()
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
    public async Task Scheduler_LoadsActiveGoalFromDatabase_AndExecutesGoalDrivenActivity()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Alchemist", "http://avatar.png", "scholarly alchemist", "Hello", "Anime")
        {
            Id = charId
        };

        var goal = new CharacterGoal(charId, "Master Arcane Herbology", CharacterGoalType.SkillDevelopment, 50, CharacterGoalPriority.High);
        goal.AddMilestone("Collect Rare Herbs", 1, 20);

        using (var db = new CoreDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterGoals.AddAsync(goal);
            await db.SaveChangesAsync();
        }

        var decisionService = new CharacterActivityDecisionService(NullLogger<CharacterActivityDecisionService>.Instance);
        var fakePipeline = new FakeSceneCompositionPipelineService();

        var forenoonTime = new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc);
        var timeBucket = CharacterActivityScheduler.GetTimeBucket(forenoonTime);

        using (var db = new CoreDbContext(_options))
        {
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var scheduler = new CharacterActivityScheduler(
                db, decisionService, fakePipeline, stateReader, NullLogger<CharacterActivityScheduler>.Instance);

            var success = await scheduler.ProcessCharacterAsync(character, forenoonTime, timeBucket);
            Assert.True(success);
        }

        // Verify activity saved with GoalId
        using (var db = new CoreDbContext(_options))
        {
            var activity = await db.CharacterActivities.FirstOrDefaultAsync(a => a.CharacterId == charId);
            Assert.NotNull(activity);
            Assert.NotNull(activity.GoalId);
            Assert.Equal(goal.Id, activity.GoalId.Value);
            Assert.Contains("Master Arcane Herbology", activity.Reason);
        }
    }
}
