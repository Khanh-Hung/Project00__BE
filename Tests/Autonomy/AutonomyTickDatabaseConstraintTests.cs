using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Services.Autonomy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tests.Autonomy;

public sealed class AutonomyTickDatabaseConstraintTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public AutonomyTickDatabaseConstraintTests()
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
    public async Task DuplicateAutonomyTick_ViolatesUniqueConstraint_AndIsClassifiedCorrectly()
    {
        var charId = Guid.NewGuid();
        var timeBucket = "2026-08-28T22:00";

        var tick1 = CharacterAutonomyTick.Create(
            characterId: charId,
            executionId: Guid.NewGuid(),
            timeBucket: timeBucket,
            status: AutonomyTickStatus.Running
        );

        var tick2 = CharacterAutonomyTick.Create(
            characterId: charId,
            executionId: Guid.NewGuid(),
            timeBucket: timeBucket,
            status: AutonomyTickStatus.Running
        );

        using (var db = new ProjectDbContext(_options))
        {
            await db.CharacterAutonomyTicks.AddAsync(tick1);
            await db.SaveChangesAsync();
        }

        using (var db = new ProjectDbContext(_options))
        {
            await db.CharacterAutonomyTicks.AddAsync(tick2);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

            // Invariant verification: IsUniqueConstraintViolation identifies constraint correctly
            Assert.True(AutonomousCharacterLifecycleOrchestrator.IsUniqueConstraintViolation(ex));
        }
    }

    [Fact]
    public void CharacterAutonomyTicks_HasUniqueIndexOn_CharacterId_And_TimeBucket()
    {
        using var db = new ProjectDbContext(_options);
        var entityType = db.Model.FindEntityType(typeof(CharacterAutonomyTick));
        Assert.NotNull(entityType);

        var indexes = entityType.GetIndexes();
        var uniqueIndex = indexes.FirstOrDefault(idx =>
            idx.IsUnique &&
            idx.Properties.Count == 2 &&
            idx.Properties.Any(p => p.Name == "CharacterId") &&
            idx.Properties.Any(p => p.Name == "TimeBucket"));

        Assert.NotNull(uniqueIndex);
    }
}
