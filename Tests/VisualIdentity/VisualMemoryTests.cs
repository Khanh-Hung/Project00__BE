using Domain.Entities;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualIdentity;

public sealed class VisualMemoryTests
{
    private static ProjectDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ProjectDbContext(options);
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
}
