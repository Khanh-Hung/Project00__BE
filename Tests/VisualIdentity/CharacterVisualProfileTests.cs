using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualIdentity;

public sealed class CharacterVisualProfileTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public CharacterVisualProfileTests()
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
    public void CreateProfile_WithInitialTraits_InitializesAtVersionOne()
    {
        var charId = Guid.NewGuid();
        var profile = new CharacterVisualProfile(
            characterId: charId,
            hairDescription: "Silver hair, medium length",
            eyeDescription: "Crimson red eyes",
            skinDescription: "Pale fair skin",
            bodyDescription: "Slender athletic build",
            distinguishingFeatures: "Small crescent scar below left eye"
        );

        Assert.Equal(charId, profile.CharacterId);
        Assert.Equal(1, profile.VisualVersion);
        Assert.Equal("Silver hair, medium length", profile.HairDescription);
        Assert.Equal("Crimson red eyes", profile.EyeDescription);
        Assert.Equal("Pale fair skin", profile.SkinDescription);
        Assert.Equal("Slender athletic build", profile.BodyDescription);
        Assert.Equal("Small crescent scar below left eye", profile.DistinguishingFeatures);
        Assert.Null(profile.PrimaryReferenceId);
        Assert.Null(profile.FaceReferenceId);
    }

    [Fact]
    public void CreateProfile_WithEmptyCharacterId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new CharacterVisualProfile(Guid.Empty));
    }

    [Fact]
    public void UpdateAppearance_MonotonicallyAdvancesVisualVersion()
    {
        var charId = Guid.NewGuid();
        var profile = new CharacterVisualProfile(charId, "Black hair", "Brown eyes");
        Assert.Equal(1, profile.VisualVersion);

        var now = DateTime.UtcNow;
        profile.UpdateAppearance("Midnight blue hair", "Glowing blue eyes", "Pale", "Tall", "None", now);
        Assert.Equal(2, profile.VisualVersion);
        Assert.Equal("Midnight blue hair", profile.HairDescription);
        Assert.Equal("Glowing blue eyes", profile.EyeDescription);

        profile.UpdateAppearance("Golden blonde hair", "Glowing blue eyes", "Pale", "Tall", "None", now.AddSeconds(1));
        Assert.Equal(3, profile.VisualVersion);
        Assert.Equal("Golden blonde hair", profile.HairDescription);
    }

    [Fact]
    public void PromoteReferenceToCanonical_MonotonicallyAdvancesVisualVersionAndSetsPointers()
    {
        var charId = Guid.NewGuid();
        var profile = new CharacterVisualProfile(charId);
        Assert.Equal(1, profile.VisualVersion);

        var refId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        profile.PromoteReferenceToCanonical(refId, isFaceOnly: false, now);

        Assert.Equal(2, profile.VisualVersion);
        Assert.Equal(refId, profile.PrimaryReferenceId);
        Assert.Equal(refId, profile.FaceReferenceId);

        var faceRefId = Guid.NewGuid();
        profile.PromoteReferenceToCanonical(faceRefId, isFaceOnly: true, now.AddSeconds(1));

        Assert.Equal(3, profile.VisualVersion);
        Assert.Equal(refId, profile.PrimaryReferenceId);
        Assert.Equal(faceRefId, profile.FaceReferenceId);
    }

    [Fact]
    public async Task SetPrimaryReference_WhenReferenceBelongsToDifferentCharacter_ThrowsInvalidOperationExceptionAndPreservesDb()
    {
        await using var db = new ProjectDbContext(_options);
        var service = new CharacterVisualProfileService(db, NullLogger<CharacterVisualProfileService>.Instance);

        var charA = Guid.NewGuid();
        var charB = Guid.NewGuid();

        // Create reference for Character B
        var refB = new CharacterVisualReference(
            characterId: charB,
            referenceUrl: "https://cdn.project00.ai/charB.png",
            type: VisualReferenceType.SecondaryCanonical,
            status: VisualReferenceStatus.Active
        );
        db.CharacterVisualReferences.Add(refB);

        var profileA = new CharacterVisualProfile(charA, "Blonde hair", "Blue eyes");
        db.CharacterVisualProfiles.Add(profileA);
        await db.SaveChangesAsync();

        // Attempt: Character A sets reference belonging to Character B
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetPrimaryReferenceAsync(charA, refB.Id));

        Assert.Contains("Cross-aggregate reference assignment rejected", ex.Message);

        // Verify Character A profile unchanged
        var profileAfter = await db.CharacterVisualProfiles.FirstAsync(p => p.CharacterId == charA);
        Assert.Null(profileAfter.PrimaryReferenceId);
        Assert.Equal(1, profileAfter.VisualVersion);
    }

    [Fact]
    public async Task ConcurrentProfileCreation_AtomicallyReturnsSingleProfileWithoutDuplicate()
    {
        var charId = Guid.NewGuid();

        // Simulate 5 concurrent worker tasks attempting to create the visual profile for the same character
        var tasks = Enumerable.Range(0, 5).Select(async _ =>
        {
            await using var taskDb = new ProjectDbContext(_options);
            var service = new CharacterVisualProfileService(taskDb, NullLogger<CharacterVisualProfileService>.Instance);
            return await service.CreateProfileAsync(charId, "Silver hair", "Red eyes");
        });

        var results = await Task.WhenAll(tasks);

        Assert.All(results, p => Assert.Equal(charId, p.CharacterId));

        // Invariant: Exactly 1 profile row in the database for this CharacterId
        await using var verifyDb = new ProjectDbContext(_options);
        var count = await verifyDb.CharacterVisualProfiles.CountAsync(p => p.CharacterId == charId);
        Assert.Equal(1, count);
    }
}
