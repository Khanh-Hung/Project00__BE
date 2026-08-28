using Application.Services;
using Domain.Entities;
using Infrastructure.BackgroundJobs;
using Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Helpers;
using Xunit;

namespace Tests.CharacterActivities;

public sealed class VisualMomentIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public VisualMomentIntegrationTests()
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
    public async Task VisualMomentActivity_ExecutesSceneCompositionPipeline_AndPersistsSceneSpecification()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Seraphina", "Princess", "http://avatar.png", "Elegant celestial princess getting ready in morning", "Hello", "Anime", worldDescription: "Sunken Palace")
        {
            Id = charId
        };

        using (var db = new ProjectDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.SaveChangesAsync();
        }

        var decisionService = new CharacterActivityDecisionService(NullLogger<CharacterActivityDecisionService>.Instance);
        var fakePipeline = new FakeSceneCompositionPipelineService();

        // Morning time (07:00) triggers GettingReady -> VisualMoment = true
        var morningTime = new DateTime(2026, 8, 28, 7, 0, 0, DateTimeKind.Utc);
        var timeBucket = CharacterActivityScheduler.GetTimeBucket(morningTime);

        using (var db = new ProjectDbContext(_options))
        {
            var scheduler = new CharacterActivityScheduler(
                db, decisionService, fakePipeline, NullLogger<CharacterActivityScheduler>.Instance);

            var result = await scheduler.ProcessCharacterAsync(character, morningTime, timeBucket);
            Assert.True(result);
        }

        // Verify Activity and SceneSpecification in DB
        using (var db = new ProjectDbContext(_options))
        {
            var activity = await db.CharacterActivities.FirstOrDefaultAsync(a => a.CharacterId == charId);
            Assert.NotNull(activity);
            Assert.True(activity.ShouldCreateVisualMoment);
            Assert.NotNull(activity.SceneIntentId);

            var sceneSpec = await db.SceneSpecifications.FirstOrDefaultAsync(s => s.CharacterId == charId);
            Assert.NotNull(sceneSpec);
            Assert.Equal("Sunken Palace", sceneSpec.Location);
        }
    }
}
