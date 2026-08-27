using Domain.Entities;
using Infrastructure.Persistence;
using Infrastructure.Services;
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

        var refB = new CharacterVisualReference(charId, "https://cdn.project00.ai/p3.png");
        db.CharacterVisualReferences.Add(refB);
        await db.SaveChangesAsync();

        var p3 = await service.SetPrimaryReferenceAsync(charId, refB.Id);
        Assert.Equal(3, p3.VisualVersion);
        Assert.Equal(refB.Id, p3.PrimaryReferenceId);

        var faceRef = new CharacterVisualReference(charId, "https://cdn.project00.ai/face.png");
        db.CharacterVisualReferences.Add(faceRef);
        await db.SaveChangesAsync();

        var p4 = await service.SetFaceReferenceAsync(charId, faceRef.Id);
        Assert.Equal(4, p4.VisualVersion);
        Assert.Equal(faceRef.Id, p4.FaceReferenceId);
    }

    [Fact]
    public async Task ConcurrentProfileUpdates_WhenWorkerUpdatesStaleVersion_ThrowsDbUpdateConcurrencyException()
    {
        var charId = Guid.NewGuid();

        // 1. Initialize profile at Version 1
        await using (var seedDb = new ProjectDbContext(_options))
        {
            var profile = new CharacterVisualProfile(charId, eyeColor: "Brown", hairColor: "Black", hairstyle: "Short", currentOutfit: "Robes");
            seedDb.CharacterVisualProfiles.Add(profile);
            await seedDb.SaveChangesAsync();
        }

        // 2. Worker A and Worker B load profile concurrently at Version 1
        await using var dbA = new ProjectDbContext(_options);
        await using var dbB = new ProjectDbContext(_options);

        var profileA = await dbA.CharacterVisualProfiles.FirstAsync(p => p.CharacterId == charId);
        var profileB = await dbB.CharacterVisualProfiles.FirstAsync(p => p.CharacterId == charId);

        Assert.Equal(1, profileA.VisualVersion);
        Assert.Equal(1, profileB.VisualVersion);

        // 3. Worker A updates appearance and commits (Version 1 -> Version 2)
        profileA.UpdateAppearance("Braided Silver Ponytail", "Royal Armor", "Natural", "Earrings", "Glowing", DateTime.UtcNow);
        await dbA.SaveChangesAsync();

        // 4. Worker B attempts to update from stale Version 1
        profileB.UpdateAppearance("Golden Waves", "Casual Outfit", "Smokey", "Necklace", "None", DateTime.UtcNow);

        // Assert: EF Core optimistic concurrency token (VisualVersion) detects stale update and throws DbUpdateConcurrencyException
        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
        Assert.NotNull(ex);

        // 5. Verify database state in separate context: remains at Version 2 with Worker A's update (no silent lost updates)
        await using var verifyDb = new ProjectDbContext(_options);
        var finalProfile = await verifyDb.CharacterVisualProfiles.FirstAsync(p => p.CharacterId == charId);

        Assert.Equal(2, finalProfile.VisualVersion);
        Assert.Equal("Braided Silver Ponytail", finalProfile.Hairstyle);
        Assert.Equal("Royal Armor", finalProfile.CurrentOutfit);
    }
}
