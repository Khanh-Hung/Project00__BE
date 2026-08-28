using Domain.Entities;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualIdentity;

public sealed class VisualMemoryTests
{
    private static CoreDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new CoreDbContext(options);
    }

    [Fact]
    public async Task RecordEvidenceAsync_SavesVisualMemoryWithFullLineage()
    {
        using var db = CreateInMemoryDb();
        var recorder = new VisualEvidenceRecorder(db, NullLogger<VisualEvidenceRecorder>.Instance);
        var charId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var memory = await recorder.RecordEvidenceAsync(
            characterId: charId,
            visualProfileVersion: 3,
            sceneRevision: 12,
            artifactId: artifactId,
            context: "Courtyard - Battle Armor",
            tags: "armor,courtyard",
            qualityScore: 0.95f,
            identityScore: 0.92f,
            featureScore: 0.88f
        );

        Assert.NotNull(memory);
        Assert.Equal(charId, memory.CharacterId);
        Assert.Equal(3, memory.VisualProfileVersion);
        Assert.Equal(12, memory.SceneRevision);
        Assert.Equal(artifactId, memory.ArtifactId);
        Assert.Equal("Courtyard - Battle Armor", memory.Context);
        Assert.Equal(0.92f, memory.IdentityScore);

        var inDb = await db.CharacterVisualMemories.FirstOrDefaultAsync(m => m.ArtifactId == artifactId);
        Assert.NotNull(inDb);
        Assert.Equal(charId, inDb.CharacterId);
    }

    [Fact]
    public async Task RecordEvidenceAsync_IsIdempotent_ForSameCharacterAndArtifact()
    {
        using var db = CreateInMemoryDb();
        var recorder = new VisualEvidenceRecorder(db, NullLogger<VisualEvidenceRecorder>.Instance);
        var charId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        var first = await recorder.RecordEvidenceAsync(
            characterId: charId,
            visualProfileVersion: 1,
            sceneRevision: 1,
            artifactId: artifactId
        );

        var second = await recorder.RecordEvidenceAsync(
            characterId: charId,
            visualProfileVersion: 1,
            sceneRevision: 1,
            artifactId: artifactId
        );

        Assert.Equal(first.Id, second.Id);

        var count = await db.CharacterVisualMemories.CountAsync(m => m.CharacterId == charId && m.ArtifactId == artifactId);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Constructor_WithInvalidInputs_ThrowsAppropriateDomainExceptions()
    {
        var charId = Guid.NewGuid();
        var artId = Guid.NewGuid();

        // Empty CharacterId
        Assert.Throws<ArgumentException>(() => new CharacterVisualMemory(Guid.Empty, 1, 1, artId));

        // Invalid VisualProfileVersion (< 1)
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterVisualMemory(charId, 0, 1, artId));

        // Invalid SceneRevision (< 1)
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterVisualMemory(charId, 1, 0, artId));

        // Empty ArtifactId
        Assert.Throws<ArgumentException>(() => new CharacterVisualMemory(charId, 1, 1, Guid.Empty));

        // QualityScore out of range (< 0 or > 1)
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterVisualMemory(charId, 1, 1, artId, qualityScore: -0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterVisualMemory(charId, 1, 1, artId, qualityScore: 1.5f));

        // IdentityScore out of range (< 0 or > 1)
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterVisualMemory(charId, 1, 1, artId, identityScore: 1.05f));

        // FeatureScore out of range (< 0 or > 1)
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterVisualMemory(charId, 1, 1, artId, featureScore: -0.01f));
    }
}
