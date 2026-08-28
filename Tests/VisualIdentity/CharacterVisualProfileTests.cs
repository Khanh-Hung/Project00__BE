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
    private readonly DbContextOptions<CoreDbContext> _options;

    public CharacterVisualProfileTests()
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
    public void CreateProfile_WithSeparatedTraits_InitializesAtVersionOne()
    {
        var charId = Guid.NewGuid();
        var profile = new CharacterVisualProfile(
            characterId: charId,
            eyeColor: "Crimson Red",
            hairColor: "Silver",
            skinTone: "Pale Fair",
            facialFeatures: "Sharp jawline, elegant high cheekbones",
            permanentMarks: "Small crescent scar below left eye",
            bodyIdentity: "Slender athletic build, 172cm",
            hairstyle: "Braided high ponytail",
            currentOutfit: "Obsidian Battle Armor",
            makeup: "Minimal natural",
            accessories: "Silver raven earring",
            temporaryAppearance: "Light dust on armor"
        );

        Assert.Equal(charId, profile.CharacterId);
        Assert.Equal(1, profile.VisualVersion);

        // Core Immutable Identity Traits
        Assert.Equal("Crimson Red", profile.EyeColor);
        Assert.Equal("Silver", profile.HairColor);
        Assert.Equal("Pale Fair", profile.SkinTone);
        Assert.Equal("Sharp jawline, elegant high cheekbones", profile.FacialFeatures);
        Assert.Equal("Small crescent scar below left eye", profile.PermanentMarks);
        Assert.Equal("Slender athletic build, 172cm", profile.BodyIdentity);

        // Mutable Appearance Traits
        Assert.Equal("Braided high ponytail", profile.Hairstyle);
        Assert.Equal("Obsidian Battle Armor", profile.CurrentOutfit);
        Assert.Equal("Silver raven earring", profile.Accessories);

        Assert.Null(profile.PrimaryReferenceId);
        Assert.Null(profile.FaceReferenceId);
    }

    [Fact]
    public void CreateProfile_WithEmptyCharacterId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new CharacterVisualProfile(Guid.Empty));
    }

    [Fact]
    public void UpdateAppearance_ModifiesMutableTraitsOnly_AndAdvancesVisualVersionMonotonically()
    {
        var charId = Guid.NewGuid();
        var profile = new CharacterVisualProfile(
            characterId: charId,
            eyeColor: "Crimson Red",
            hairColor: "Silver",
            hairstyle: "High ponytail",
            currentOutfit: "Battle Armor"
        );
        Assert.Equal(1, profile.VisualVersion);

        var now = DateTime.UtcNow;
        profile.UpdateAppearance(
            hairstyle: "Loose long waves",
            currentOutfit: "Royal Gala Gown",
            makeup: "Smokey eye",
            accessories: "Diamond Tiara",
            temporaryAppearance: "Perfumed scent, glowing tiara",
            now: now
        );

        Assert.Equal(2, profile.VisualVersion);

        // Core Identity unchanged
        Assert.Equal("Crimson Red", profile.EyeColor);
        Assert.Equal("Silver", profile.HairColor);

        // Mutable Appearance updated
        Assert.Equal("Loose long waves", profile.Hairstyle);
        Assert.Equal("Royal Gala Gown", profile.CurrentOutfit);
        Assert.Equal("Diamond Tiara", profile.Accessories);
    }

    [Fact]
    public void RefineCoreIdentity_ExplicitDomainMethod_AdvancesVisualVersionMonotonically()
    {
        var charId = Guid.NewGuid();
        var profile = new CharacterVisualProfile(
            characterId: charId,
            eyeColor: "Blue",
            hairColor: "Blonde"
        );
        Assert.Equal(1, profile.VisualVersion);

        var now = DateTime.UtcNow;
        profile.RefineCoreIdentity(
            eyeColor: "Sapphire Blue",
            hairColor: "Platinum Blonde",
            skinTone: "Porcelain",
            facialFeatures: "Elven angular features",
            permanentMarks: "Runic mark on neck",
            bodyIdentity: "Graceful tall build",
            now: now
        );

        Assert.Equal(2, profile.VisualVersion);
        Assert.Equal("Sapphire Blue", profile.EyeColor);
        Assert.Equal("Platinum Blonde", profile.HairColor);
        Assert.Equal("Runic mark on neck", profile.PermanentMarks);
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
        await using var db = new CoreDbContext(_options);
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

        var profileA = new CharacterVisualProfile(charA, eyeColor: "Blue", hairColor: "Blonde");
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
            await using var taskDb = new CoreDbContext(_options);
            var service = new CharacterVisualProfileService(taskDb, NullLogger<CharacterVisualProfileService>.Instance);
            return await service.CreateProfileAsync(charId, eyeColor: "Red", hairColor: "Silver");
        });

        var results = await Task.WhenAll(tasks);

        Assert.All(results, p => Assert.Equal(charId, p.CharacterId));

        // Invariant: Exactly 1 profile row in the database for this CharacterId
        await using var verifyDb = new CoreDbContext(_options);
        var count = await verifyDb.CharacterVisualProfiles.CountAsync(p => p.CharacterId == charId);
        Assert.Equal(1, count);
    }
}
