using System.Text.Json;
using Application.Services;
using Domain.Entities;
using Infrastructure.BackgroundJobs;
using Infrastructure.Persistence;
using Infrastructure.Services.Scene;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Helpers;
using Xunit;

namespace Tests.CharacterActivities;

public sealed class AuthoritativeStateSchedulerIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public AuthoritativeStateSchedulerIntegrationTests()
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
    public async Task Scheduler_LoadsAuthoritativeCurrentVisualState_Location_AndSceneRevision()
    {
        var charId = Guid.NewGuid();
        var character = new Character(
            name: "Valerius",
            title: "Alchemist",
            avatarUrl: "http://avatar.png",
            personalityPrompt: "morning routine getting ready",
            greeting: "Hello",
            category: "Anime",
            customMilestonesJson: JsonSerializer.Serialize(new[] { "Synthesize Philosopher Stone" }),
            worldDescription: "A massive continent of floating islands" // NOT the location!
        )
        {
            Id = charId
        };

        // Seed Authoritative SceneVisualState at Revision 5 in "Crystal Conservatory" with "Royal Alchemist Robe"
        var charVisualState = new CharacterVisualState(
            characterId: charId,
            location: "Crystal Conservatory",
            sceneRevision: 5,
            outfit: "Royal Alchemist Robe",
            hairstyle: "Braided Silver Ponytail"
        );

        var sceneVisualState = new SceneVisualState(
            sessionId: Guid.NewGuid(),
            characterId: charId,
            location: "Crystal Conservatory",
            characterState: charVisualState,
            sceneRevision: 5,
            sceneKey: "crystal_conservatory"
        );

        var stateJson = JsonSerializer.Serialize(sceneVisualState);
        var stateRecord = new SceneVisualStateRecord(
            sessionId: sceneVisualState.SessionId,
            characterId: charId,
            sceneKey: "crystal_conservatory",
            sceneRevision: 5,
            stateJson: stateJson,
            fingerprint: sceneVisualState.Fingerprint,
            version: 1
        );

        using (var db = new CoreDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.SceneVisualStates.AddAsync(stateRecord);
            await db.SaveChangesAsync();
        }

        var decisionService = new CharacterActivityDecisionService(NullLogger<CharacterActivityDecisionService>.Instance);
        var fakePipeline = new FakeSceneCompositionPipelineService();

        var morningTime = new DateTime(2026, 8, 28, 7, 0, 0, DateTimeKind.Utc);
        var timeBucket = CharacterActivityScheduler.GetTimeBucket(morningTime);

        using (var db = new CoreDbContext(_options))
        {
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var scheduler = new CharacterActivityScheduler(
                db, decisionService, fakePipeline, stateReader, NullLogger<CharacterActivityScheduler>.Instance);

            var result = await scheduler.ProcessCharacterAsync(character, morningTime, timeBucket);
            Assert.True(result);
        }

        // Verify: Activity took authoritative location "Crystal Conservatory" rather than worldDescription
        using (var db = new CoreDbContext(_options))
        {
            var activity = await db.CharacterActivities.FirstOrDefaultAsync(a => a.CharacterId == charId);
            Assert.NotNull(activity);
            Assert.Equal("Crystal Conservatory", activity.Location);
            Assert.True(activity.ShouldCreateVisualMoment);
            Assert.NotNull(activity.SceneIntentId);

            var sceneSpec = await db.SceneSpecifications.FirstOrDefaultAsync(s => s.CharacterId == charId);
            Assert.NotNull(sceneSpec);
            Assert.Equal("Crystal Conservatory", sceneSpec.Location);
            Assert.Equal(5, sceneSpec.SceneRevision); // Inherited authoritative revision 5!
        }
    }
}
