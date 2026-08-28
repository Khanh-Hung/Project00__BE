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

public sealed class ActivitySchedulerIdempotencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public ActivitySchedulerIdempotencyTests()
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
    public async Task RepeatedSchedulerExecution_ProducesExactlyOneActivityRecord()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Alchemist", "http://avatar.png", "Stoic scholar alchemist", "Hello", "Anime", worldDescription: "Arcane Laboratory")
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

        var testTime = new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc);

        // Run 1
        using (var db = new ProjectDbContext(_options))
        {
            var scheduler = new CharacterActivityScheduler(
                db, decisionService, fakePipeline, NullLogger<CharacterActivityScheduler>.Instance);

            var processed = await scheduler.ExecuteCycleAsync(currentTime: testTime);
            Assert.Equal(1, processed);
        }

        // Run 2 (Same time bucket)
        using (var db = new ProjectDbContext(_options))
        {
            var scheduler = new CharacterActivityScheduler(
                db, decisionService, fakePipeline, NullLogger<CharacterActivityScheduler>.Instance);

            var processed = await scheduler.ExecuteCycleAsync(currentTime: testTime);
            Assert.Equal(0, processed); // Already processed for this time bucket
        }

        // Verify total DB count
        using (var db = new ProjectDbContext(_options))
        {
            var activities = await db.CharacterActivities.Where(a => a.CharacterId == charId).ToListAsync();
            Assert.Single(activities);
        }
    }
}
