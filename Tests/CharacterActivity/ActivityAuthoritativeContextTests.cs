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

public sealed class ActivityAuthoritativeContextTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public ActivityAuthoritativeContextTests()
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
    public async Task Scheduler_UsesAuthoritativeSceneStateLocation_AndSceneRevision()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Eldrin", "Stargazer", "http://avatar.png", "scholarly astronomer", "Hello", "Anime", worldDescription: "Fantasy World With Distant Galaxies")
        {
            Id = charId
        };

        var charVisualState = new CharacterVisualState(charId, "Grand Observatory", 4, outfit: "Starlight Robe");
        var sceneVisualState = new SceneVisualState(
            sessionId: Guid.NewGuid(),
            characterId: charId,
            location: "Grand Observatory",
            characterState: charVisualState,
            sceneRevision: 4
        );

        using (var db = new CoreDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            await stateReader.SaveStateAsync(sceneVisualState);
        }

        var decisionService = new CharacterActivityDecisionService(NullLogger<CharacterActivityDecisionService>.Instance);
        var fakePipeline = new FakeSceneCompositionPipelineService();

        var nightTime = new DateTime(2026, 8, 28, 23, 0, 0, DateTimeKind.Utc);
        var timeBucket = CharacterActivityScheduler.GetTimeBucket(nightTime);

        using (var db = new CoreDbContext(_options))
        {
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var scheduler = new CharacterActivityScheduler(
                db, decisionService, fakePipeline, stateReader, NullLogger<CharacterActivityScheduler>.Instance);

            var result = await scheduler.ProcessCharacterAsync(character, nightTime, timeBucket);
            Assert.True(result);
        }

        // Verify Activity uses "Grand Observatory" (from visual state), NOT WorldDescription!
        using (var db = new CoreDbContext(_options))
        {
            var activity = await db.CharacterActivities.FirstOrDefaultAsync(a => a.CharacterId == charId);
            Assert.NotNull(activity);
            Assert.Equal("Grand Observatory", activity.Location);
            Assert.NotEqual("Fantasy World With Distant Galaxies", activity.Location);
        }
    }

    [Fact]
    public void IsUniqueConstraintViolation_IdentifiesUniqueConstraintErrors_AndRejectsOtherErrors()
    {
        // 1. Unique constraint violation message
        var uniqueEx = new DbUpdateException("Error", new Exception("UNIQUE constraint failed: CharacterActivities.CharacterId, CharacterActivities.TimeBucket"));
        Assert.True(CharacterActivityScheduler.IsUniqueConstraintViolation(uniqueEx));

        // 2. PostgreSQL 23505 unique error
        var pgEx = new DbUpdateException("23505: duplicate key value violates unique constraint \"IX_CharacterActivities_CharacterId_TimeBucket\"", new Exception("23505 unique"));
        Assert.True(CharacterActivityScheduler.IsUniqueConstraintViolation(pgEx));

        // 3. Foreign Key violation message -> Must NOT be identified as unique violation!
        var fkEx = new DbUpdateException("Foreign key violation", new Exception("FOREIGN KEY constraint failed"));
        Assert.False(CharacterActivityScheduler.IsUniqueConstraintViolation(fkEx));

        // 4. NOT NULL violation message -> Must NOT be identified as unique violation!
        var notNullEx = new DbUpdateException("Not null violation", new Exception("NOT NULL constraint failed: CharacterActivities.Location"));
        Assert.False(CharacterActivityScheduler.IsUniqueConstraintViolation(notNullEx));

        // 5. Connection drop -> Must NOT be identified as unique violation!
        var connEx = new DbUpdateException("Connection timeout", new TimeoutException("Database connection timed out"));
        Assert.False(CharacterActivityScheduler.IsUniqueConstraintViolation(connEx));
    }
}
