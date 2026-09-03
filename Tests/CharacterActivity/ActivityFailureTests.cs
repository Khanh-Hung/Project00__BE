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

namespace Tests.CharacterActivities;

public sealed class ActivityFailureTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public ActivityFailureTests()
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
    public async Task CompositionPipelineFailure_PreservesValidActivityRecord_WithoutCorruptingState()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Alchemist", "http://avatar.png", "morning routine and grooming", "Hello", "Anime", worldDescription: "Alchemical Workshop")
        {
            Id = charId
        };

        using (var db = new CoreDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.CharacterStates.AddAsync(new CharacterState(charId, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var decisionService = new CharacterActivityDecisionService(NullLogger<CharacterActivityDecisionService>.Instance);
        var failingPipeline = new FakeSceneCompositionPipelineService(shouldFail: true);

        var morningTime = new DateTime(2026, 8, 28, 7, 0, 0, DateTimeKind.Utc);
        var timeBucket = CharacterActivityScheduler.GetTimeBucket(morningTime);

        using (var db = new CoreDbContext(_options))
        {
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var scheduler = new CharacterActivityScheduler(
                db, decisionService, failingPipeline, stateReader, NullLogger<CharacterActivityScheduler>.Instance);

            // Must NOT throw uncaught exception; returns true for activity creation
            var result = await scheduler.ProcessCharacterAsync(character, morningTime, timeBucket);
            Assert.True(result);
        }

        // Verify Activity record remains valid and active in DB
        using (var db = new CoreDbContext(_options))
        {
            var activity = await db.CharacterActivities.FirstOrDefaultAsync(a => a.CharacterId == charId);
            Assert.NotNull(activity);
            Assert.True(activity.Status == CharacterActivityStatus.Started || activity.Status == CharacterActivityStatus.Completed);

            // No scene spec was persisted due to downstream failure
            var sceneSpecs = await db.SceneSpecifications.Where(s => s.CharacterId == charId).ToListAsync();
            Assert.Empty(sceneSpecs);
        }
    }
}
