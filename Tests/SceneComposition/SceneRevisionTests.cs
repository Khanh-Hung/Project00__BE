using Domain.Entities;
using Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tests.SceneComposition;

public sealed class SceneRevisionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public SceneRevisionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new CoreDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public void SceneRevision_IsStrictlyIsolated_Revision1DoesNotEqualRevision2()
    {
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var rev1 = new SceneSpecification(charId, "Library", "Reading", sceneRevision: 1, sessionId: sessionId, turnId: turnId);
        var rev2 = new SceneSpecification(charId, "Library", "Standing up", sceneRevision: 2, sessionId: sessionId, turnId: turnId);

        Assert.Equal(1, rev1.SceneRevision);
        Assert.Equal(2, rev2.SceneRevision);
        Assert.NotEqual(rev1.Id, rev2.Id);
    }

    [Fact]
    public async Task UniqueConstraint_EnforcesSingleSceneSpecificationPerRevisionInDb()
    {
        await using var db = new CoreDbContext(_options);
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var spec1 = new SceneSpecification(charId, "Library", "Reading", sceneRevision: 1, sessionId: sessionId, turnId: turnId);
        var spec2 = new SceneSpecification(charId, "Library", "Reading duplicate", sceneRevision: 1, sessionId: sessionId, turnId: turnId);

        db.SceneSpecifications.Add(spec1);
        await db.SaveChangesAsync();

        db.SceneSpecifications.Add(spec2);

        // Assert: Unique index (CharacterId, SessionId, TurnId, SceneRevision) rejects duplicate
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
