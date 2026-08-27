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

        var p1 = await service.CreateProfileAsync(charId, eyeColor: "Brown", hairColor: "Black");
        Assert.Equal(1, p1.VisualVersion);

        var p2 = await service.UpdateAppearanceAsync(charId, hairstyle: "Silver hair", makeup: "Red eyes");
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
    public async Task ConcurrentProfileUpdates_WhenTwoWorkersRaceFromVersion5_WorkerAWinsToVersion6_AndWorkerBThrowsDbUpdateConcurrencyException()
    {
        var charId = Guid.NewGuid();

        // 1. Initialize profile and advance sequentially to Version 5
        await using (var seedDb = new ProjectDbContext(_options))
        {
            var profile = new CharacterVisualProfile(
                characterId: charId,
                eyeColor: "Crimson",
                hairColor: "Silver",
                hairstyle: "Version 1 Hairstyle",
                visualVersion: 5
            );
            seedDb.CharacterVisualProfiles.Add(profile);
            await seedDb.SaveChangesAsync();
        }

        // 2. Worker A and Worker B load profile concurrently at Version 5
        await using var dbA = new ProjectDbContext(_options);
        await using var dbB = new ProjectDbContext(_options);

        var profileA = await dbA.CharacterVisualProfiles.FirstAsync(p => p.CharacterId == charId);
        var profileB = await dbB.CharacterVisualProfiles.FirstAsync(p => p.CharacterId == charId);

        Assert.Equal(5, profileA.VisualVersion);
        Assert.Equal(5, profileB.VisualVersion);

        // 3. Worker A updates appearance and commits (Version 5 -> Version 6)
        profileA.UpdateAppearance(
            hairstyle: "Worker A Hairstyle",
            currentOutfit: "Worker A Armor",
            makeup: "Natural",
            accessories: "Cloak",
            temporaryAppearance: "Light dust",
            now: DateTime.UtcNow
        );
        await dbA.SaveChangesAsync();

        // 4. Worker B attempts to update from stale Version 5 (trying 5 -> 6 concurrently)
        profileB.UpdateAppearance(
            hairstyle: "Worker B Hairstyle",
            currentOutfit: "Worker B Robes",
            makeup: "Smokey",
            accessories: "Tiara",
            temporaryAppearance: "None",
            now: DateTime.UtcNow
        );

        // Assert: EF Core optimistic concurrency token (VisualVersion) detects stale update and throws DbUpdateConcurrencyException
        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
        Assert.NotNull(ex);

        // 5. Verify database state in independent context: remains at Version 6 with Worker A's authoritative update (no lost updates)
        await using var verifyDb = new ProjectDbContext(_options);
        var finalProfile = await verifyDb.CharacterVisualProfiles.FirstAsync(p => p.CharacterId == charId);

        Assert.Equal(6, finalProfile.VisualVersion);
        Assert.Equal("Worker A Hairstyle", finalProfile.Hairstyle);
        Assert.Equal("Worker A Armor", finalProfile.CurrentOutfit);
    }

    [Fact]
    public async Task ParallelProfileUpdates_UnderContention_ExactlyOneWorkerSucceedsPerRace()
    {
        var charId = Guid.NewGuid();

        // 1. Initialize profile at Version 1
        await using (var seedDb = new ProjectDbContext(_options))
        {
            var profile = new CharacterVisualProfile(charId, eyeColor: "Amber", hairColor: "Raven", visualVersion: 1);
            seedDb.CharacterVisualProfiles.Add(profile);
            await seedDb.SaveChangesAsync();
        }

        // 2. Pre-load profile across 10 independent contexts while at Version 1
        var contexts = new List<ProjectDbContext>();
        var profiles = new List<CharacterVisualProfile>();

        for (int i = 0; i < 10; i++)
        {
            var ctx = new ProjectDbContext(_options);
            var p = await ctx.CharacterVisualProfiles.FirstAsync(x => x.CharacterId == charId);
            Assert.Equal(1, p.VisualVersion);
            p.UpdateAppearance($"Style {i}", $"Outfit {i}", null, null, null, DateTime.UtcNow);
            contexts.Add(ctx);
            profiles.Add(p);
        }

        var successCount = 0;
        var concurrencyConflictCount = 0;

        // 3. All 10 contexts race to commit their update simultaneously
        var tasks = contexts.Select(async ctx =>
        {
            try
            {
                await ctx.SaveChangesAsync();
                Interlocked.Increment(ref successCount);
            }
            catch (DbUpdateConcurrencyException)
            {
                Interlocked.Increment(ref concurrencyConflictCount);
            }
            finally
            {
                await ctx.DisposeAsync();
            }
        });

        await Task.WhenAll(tasks);

        // Invariant: Exactly 1 worker wins the race, and 9 workers encounter DbUpdateConcurrencyException
        Assert.Equal(1, successCount);
        Assert.Equal(9, concurrencyConflictCount);

        // Verify final DB state is Version 2
        await using var verifyDb = new ProjectDbContext(_options);
        var finalProfile = await verifyDb.CharacterVisualProfiles.FirstAsync(p => p.CharacterId == charId);
        Assert.Equal(2, finalProfile.VisualVersion);
    }
}
