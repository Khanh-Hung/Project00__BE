using Application.Contracts.Activities;
using Application.Contracts.Autonomous;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services.Autonomous;
using Infrastructure.Services.Goals;
using Infrastructure.Services.Scene;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Helpers;
using Xunit;

namespace Tests.AutonomousLoop;

public sealed class AutonomousFailureMatrixTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public AutonomousFailureMatrixTests()
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

    [Theory]
    [InlineData("23505: duplicate key value violates unique constraint \"IX_CharacterActivities_CharacterId_TimeBucket\"", true)]
    [InlineData("SQLite Error 19: 'UNIQUE constraint failed: CharacterActivities.CharacterId, CharacterActivities.TimeBucket'.", true)]
    [InlineData("23503: insert or update on table \"CharacterActivities\" violates foreign key constraint", false)]
    [InlineData("23502: null value in column \"CharacterId\" of relation \"CharacterActivities\" violates not-null constraint", false)]
    [InlineData("40001: could not serialize access due to concurrent update", false)]
    [InlineData("Connection refused: database server down", false)]
    public void IsUniqueConstraintViolation_AccuratelyClassifies_PostgresAndSqliteErrors(string errorMessage, bool expectedIsUniqueViolation)
    {
        var innerEx = new Exception(errorMessage);
        var dbUpdateEx = new DbUpdateException(errorMessage, innerEx);

        var result = ActivityExecutionService.IsUniqueConstraintViolation(dbUpdateEx);

        Assert.Equal(expectedIsUniqueViolation, result);
    }

    [Fact]
    public async Task DbConnectionFailure_IsRethrown_AndNotSwallowed()
    {
        var brokenConnection = new SqliteConnection("Filename=nonexistent_broken_db.db");
        var brokenOptions = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(brokenConnection)
            .Options;

        using var brokenDb = new CoreDbContext(brokenOptions);
        var goalService = new GoalProgressService(brokenDb, NullLogger<GoalProgressService>.Instance);
        var fakePipeline = new FakeSceneCompositionPipelineService();
        var stateReader = new SceneVisualStateReader(brokenDb, NullLogger<SceneVisualStateReader>.Instance);
        var execService = new ActivityExecutionService(brokenDb, goalService, fakePipeline, stateReader, new Infrastructure.Services.State.CharacterStateTransitionService(brokenDb, Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Services.State.CharacterStateTransitionService>.Instance), Microsoft.Extensions.Logging.Abstractions.NullLogger<ActivityExecutionService>.Instance);

        var character = new Character("Valerius", "Alchemist", "http://avatar.png", "Scholar", "Hello", "Anime");
        var candidate = new CharacterActivityCandidate(
            ActivityType: CharacterActivityType.Reading,
            Location: "Library",
            Reason: "Reading",
            Priority: ActivityPriority.Normal,
            DurationMinutes: 30,
            ShouldCreateVisualMoment: false,
            Confidence: 0.95f,
            ActionHint: "reading",
            PoseHint: "seated",
            OutfitHint: null,
            EnvironmentHint: null,
            DecisionFingerprint: "fingerprint-fail-001"
        );

        var request = new ActivityExecutionRequest(character, candidate, DateTime.UtcNow, "2026-08-28T12:00");

        await Assert.ThrowsAnyAsync<Exception>(() => execService.ExecuteActivityAsync(request));
    }

    [Fact]
    public async Task Cancellation_AbortsExecution_WithoutPartialStateDrift()
    {
        var charId = Guid.NewGuid();
        var character = new Character("Valerius", "Alchemist", "http://avatar.png", "Scholar", "Hello", "Anime")
        {
            Id = charId
        };

        using (var db = new CoreDbContext(_options))
        {
            await db.Characters.AddAsync(character);
            await db.SaveChangesAsync();
        }

        var candidate = new CharacterActivityCandidate(
            ActivityType: CharacterActivityType.Working,
            Location: "Lab",
            Reason: "Working",
            Priority: ActivityPriority.Normal,
            DurationMinutes: 30,
            ShouldCreateVisualMoment: false,
            Confidence: 0.95f,
            ActionHint: "working",
            PoseHint: "seated",
            OutfitHint: null,
            EnvironmentHint: null,
            DecisionFingerprint: "fingerprint-cancel-001"
        );

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-canceled token

        var request = new ActivityExecutionRequest(character, candidate, DateTime.UtcNow, "2026-08-28T16:00");

        using (var db = new CoreDbContext(_options))
        {
            var goalService = new GoalProgressService(db, NullLogger<GoalProgressService>.Instance);
            var fakePipeline = new FakeSceneCompositionPipelineService();
            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var execService = new ActivityExecutionService(db, goalService, fakePipeline, stateReader, new Infrastructure.Services.State.CharacterStateTransitionService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Services.State.CharacterStateTransitionService>.Instance), Microsoft.Extensions.Logging.Abstractions.NullLogger<ActivityExecutionService>.Instance);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execService.ExecuteActivityAsync(request, cts.Token));
        }

        // Verify DB State: 0 activities inserted
        using (var db = new CoreDbContext(_options))
        {
            var count = await db.CharacterActivities.CountAsync(a => a.CharacterId == charId);
            Assert.Equal(0, count);
        }
    }
}
