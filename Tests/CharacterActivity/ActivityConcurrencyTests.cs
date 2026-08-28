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

public sealed class ActivityConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public ActivityConcurrencyTests()
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
    public async Task TenConcurrentWorkers_AllowsExactlyOneWinner_AndSuppressesNineDuplicates()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Alchemist", "http://avatar.png", "Stoic scholar alchemist", "Hello", "Anime", worldDescription: "Arcane Laboratory")
        {
            Id = charId
        };

        using (var db = new CoreDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.SaveChangesAsync();
        }

        var decisionService = new CharacterActivityDecisionService(NullLogger<CharacterActivityDecisionService>.Instance);
        var fakePipeline = new FakeSceneCompositionPipelineService();
        var testTime = new DateTime(2026, 8, 28, 14, 0, 0, DateTimeKind.Utc);
        var timeBucket = CharacterActivityScheduler.GetTimeBucket(testTime);

        // 10 concurrent workers trying to process the same character simultaneously
        var tasks = Enumerable.Range(1, 10).Select(async workerId =>
        {
            await using var workerDb = new CoreDbContext(_options);
            var stateReader = new SceneVisualStateReader(workerDb, NullLogger<SceneVisualStateReader>.Instance);
            var scheduler = new CharacterActivityScheduler(
                workerDb, decisionService, fakePipeline, stateReader, NullLogger<CharacterActivityScheduler>.Instance);

            return await scheduler.ProcessCharacterAsync(character, testTime, timeBucket);
        }).ToList();

        var results = await Task.WhenAll(tasks);

        int winners = results.Count(r => r == true);
        int suppressed = results.Count(r => r == false);

        Assert.Equal(1, winners);
        Assert.Equal(9, suppressed);

        // Assert DB Invariant: Exactly 1 record exists in DB
        using (var db = new CoreDbContext(_options))
        {
            var count = await db.CharacterActivities.CountAsync(a => a.CharacterId == charId && a.TimeBucket == timeBucket);
            Assert.Equal(1, count);
        }
    }
}
