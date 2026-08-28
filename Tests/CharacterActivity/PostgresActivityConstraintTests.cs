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

public sealed class PostgresActivityConstraintTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public PostgresActivityConstraintTests()
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

    [Theory]
    [InlineData("23505: duplicate key value violates unique constraint \"IX_CharacterActivities_CharacterId_TimeBucket\"", true)]
    [InlineData("SQLite Error 19: 'UNIQUE constraint failed: CharacterActivities.CharacterId, CharacterActivities.TimeBucket'.", true)]
    [InlineData("duplicate key error on IX_CharacterActivities_CharacterId_TimeBucket", true)]
    [InlineData("23503: insert or update on table \"CharacterActivities\" violates foreign key constraint", false)]
    [InlineData("23502: null value in column \"Location\" of relation \"CharacterActivities\" violates not-null constraint", false)]
    [InlineData("Connection refused: database server down", false)]
    [InlineData("40001: could not serialize access due to concurrent update", false)]
    public void IsUniqueConstraintViolation_AccuratelyClassifies_PostgresAndSqliteErrors(string errorMessage, bool expectedIsUniqueViolation)
    {
        var innerEx = new Exception(errorMessage);
        var dbUpdateEx = new DbUpdateException(errorMessage, innerEx);

        var result = CharacterActivityScheduler.IsUniqueConstraintViolation(dbUpdateEx);

        Assert.Equal(expectedIsUniqueViolation, result);
    }

    [Fact]
    public async Task NonUniqueDbUpdateException_IsRethrown_AndNotSwallowed()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Alchemist", "http://avatar.png", "scholar", "Hello", "Anime")
        {
            Id = charId
        };

        var decisionService = new CharacterActivityDecisionService(NullLogger<CharacterActivityDecisionService>.Instance);
        var fakePipeline = new FakeSceneCompositionPipelineService();

        var testTime = new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc);
        var timeBucket = CharacterActivityScheduler.GetTimeBucket(testTime);

        // Intentionally corrupt db options with broken connection to cause non-unique exception
        var brokenConnection = new SqliteConnection("Filename=nonexistent_broken.db");
        var brokenOptions = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(brokenConnection)
            .Options;

        using var brokenDb = new ProjectDbContext(brokenOptions);
        var stateReader = new SceneVisualStateReader(brokenDb, NullLogger<SceneVisualStateReader>.Instance);
        var scheduler = new CharacterActivityScheduler(
            brokenDb, decisionService, fakePipeline, stateReader, NullLogger<CharacterActivityScheduler>.Instance);

        // Must rethrow the non-unique DbUpdateException / SqliteException
        await Assert.ThrowsAnyAsync<Exception>(() => scheduler.ProcessCharacterAsync(character, testTime, timeBucket));
    }

    [Fact]
    public void ModelMetadata_VerifiesPartialUniqueIndex_AndCompositeIndex()
    {
        using var db = new ProjectDbContext(_options);
        var entityType = db.Model.FindEntityType(typeof(CharacterActivity));
        Assert.NotNull(entityType);

        // 1. Verify Unique Partial Index on (CharacterId, TimeBucket) with filter
        var timeBucketIndex = entityType.GetIndexes()
            .FirstOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "CharacterId", "TimeBucket" }));
        Assert.NotNull(timeBucketIndex);
        Assert.True(timeBucketIndex.IsUnique);
        Assert.Equal("\"Source\" = 'Autonomous'", timeBucketIndex.GetFilter());

        // 2. Verify Composite Index on (CharacterId, CreatedAt)
        var createdAtIndex = entityType.GetIndexes()
            .FirstOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "CharacterId", "CreatedAt" }));
        Assert.NotNull(createdAtIndex);
        Assert.Equal(2, createdAtIndex.Properties.Count);
    }
}
