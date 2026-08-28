using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Services.Reactions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tests.CharacterReaction;

public sealed class PostgresWorldEventConstraintTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public PostgresWorldEventConstraintTests()
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
    public async Task DuplicateWorldEventReaction_ViolatesUniqueConstraint_AndIsClassifiedCorrectly()
    {
        var charId = Guid.NewGuid();
        var worldEventId = Guid.NewGuid();

        var reaction1 = CharacterWorldEventReaction.Create(
            characterId: charId,
            worldEventId: worldEventId,
            executionId: Guid.NewGuid(),
            perceptionType: PerceptionType.PositiveSocialFeedback,
            priority: ReactionPriority.DirectUserInteraction
        );

        var reaction2 = CharacterWorldEventReaction.Create(
            characterId: charId,
            worldEventId: worldEventId,
            executionId: Guid.NewGuid(),
            perceptionType: PerceptionType.PositiveSocialFeedback,
            priority: ReactionPriority.DirectUserInteraction
        );

        using (var db = new ProjectDbContext(_options))
        {
            await db.CharacterWorldEventReactions.AddAsync(reaction1);
            await db.SaveChangesAsync();
        }

        using (var db = new ProjectDbContext(_options))
        {
            await db.CharacterWorldEventReactions.AddAsync(reaction2);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

            // Invariant verification: IsUniqueConstraintViolation identifies constraint correctly
            Assert.True(CharacterReactionExecutionService.IsUniqueConstraintViolation(ex));
        }
    }
}
