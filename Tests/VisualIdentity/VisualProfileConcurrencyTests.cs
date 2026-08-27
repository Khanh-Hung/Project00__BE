using Application.Services;
using Domain.Entities;
using Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualIdentity;

public sealed class VisualProfileConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public VisualProfileConcurrencyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new ProjectDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public async Task SequentialProfileUpdates_StrictlyAdvanceVisualVersionMonotonically()
    {
        await using var db = new ProjectDbContext(_options);
        var service = new CharacterVisualProfileService(db, NullLogger<CharacterVisualProfileService>.Instance);
        var charId = Guid.NewGuid();

        var p1 = await service.CreateProfileAsync(charId, "Black hair", "Brown eyes");
        Assert.Equal(1, p1.VisualVersion);

        var p2 = await service.UpdateAppearanceAsync(charId, "Silver hair", "Red eyes", "Pale", "Athletic", "Scar");
        Assert.Equal(2, p2.VisualVersion);

        var refId = Guid.NewGuid();
        var p3 = await service.SetPrimaryReferenceAsync(charId, refId);
        Assert.Equal(3, p3.VisualVersion);
        Assert.Equal(refId, p3.PrimaryReferenceId);

        var faceId = Guid.NewGuid();
        var p4 = await service.SetFaceReferenceAsync(charId, faceId);
        Assert.Equal(4, p4.VisualVersion);
        Assert.Equal(faceId, p4.FaceReferenceId);
    }
}
