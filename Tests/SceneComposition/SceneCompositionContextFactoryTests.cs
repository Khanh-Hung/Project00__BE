using Application.DTOs;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Services.Scene;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.SceneComposition;

public sealed class SceneCompositionContextFactoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public SceneCompositionContextFactoryTests()
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
    public async Task CreateContextAsync_HydratesFromAllReaders_AndFiltersQuarantinedAndDeletedArtifacts()
    {
        await using var db = new CoreDbContext(_options);
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        // 1. Seed Profile & Reference
        var profile = new CharacterVisualProfile(
            characterId: charId,
            eyeColor: "Emerald Green",
            hairColor: "Golden Blonde",
            currentOutfit: "Scholar Uniform"
        );
        db.CharacterVisualProfiles.Add(profile);

        var canonicalRef = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/canonical.png",
            type: VisualReferenceType.Canonical,
            status: VisualReferenceStatus.Active,
            isCanonical: true
        );
        db.CharacterVisualReferences.Add(canonicalRef);

        // 2. Seed SceneImages with different lifecycles
        var validImageId = Guid.NewGuid();
        var quarantinedImageId = Guid.NewGuid();
        var deletedImageId = Guid.NewGuid();

        var validImage = new SceneImage(
            sessionId: sessionId,
            characterId: charId,
            turnId: turnId,
            sceneRevision: 1,
            imageUrl: "https://cdn.project00.ai/valid.png",
            prompt: "valid prompt",
            generationRequestId: Guid.NewGuid(),
            visualRevision: 1,
            lifecycleStatus: ArtifactLifecycleStatus.Current,
            id: validImageId
        );

        var quarantinedImage = new SceneImage(
            sessionId: sessionId,
            characterId: charId,
            turnId: turnId,
            sceneRevision: 2,
            imageUrl: "https://cdn.project00.ai/quarantined.png",
            prompt: "quarantined prompt",
            generationRequestId: Guid.NewGuid(),
            visualRevision: 2,
            lifecycleStatus: ArtifactLifecycleStatus.Quarantined,
            id: quarantinedImageId
        );

        var deletedImage = new SceneImage(
            sessionId: sessionId,
            characterId: charId,
            turnId: turnId,
            sceneRevision: 3,
            imageUrl: "https://cdn.project00.ai/deleted.png",
            prompt: "deleted prompt",
            generationRequestId: Guid.NewGuid(),
            visualRevision: 3,
            lifecycleStatus: ArtifactLifecycleStatus.Deleted,
            id: deletedImageId
        );

        db.SceneImages.AddRange(validImage, quarantinedImage, deletedImage);

        // 3. Seed Memories
        var validMemory = new CharacterVisualMemory(
            characterId: charId,
            visualProfileVersion: 1,
            sceneRevision: 1,
            artifactId: validImageId,
            context: "Library study session"
        );

        var quarantinedMemory = new CharacterVisualMemory(
            characterId: charId,
            visualProfileVersion: 1,
            sceneRevision: 2,
            artifactId: quarantinedImageId,
            context: "Failed quarantine scene"
        );

        var deletedMemory = new CharacterVisualMemory(
            characterId: charId,
            visualProfileVersion: 1,
            sceneRevision: 3,
            artifactId: deletedImageId,
            context: "Deleted old scene"
        );

        db.CharacterVisualMemories.AddRange(validMemory, quarantinedMemory, deletedMemory);

        // 4. Seed Previous Scene
        var prevScene = new SceneSpecification(
            characterId: charId,
            location: "Library",
            action: "Studying",
            sceneRevision: 1,
            sessionId: sessionId,
            turnId: turnId
        );
        db.SceneSpecifications.Add(prevScene);

        await db.SaveChangesAsync();

        // 5. Build Factory & Execute
        var profileReader = new CharacterVisualProfileReader(db);
        var canonicalReader = new CanonicalReferenceReader(db);
        var memoryReader = new VisualMemoryReader(db);
        var previousSceneReader = new PreviousSceneReader(db);

        var factory = new SceneCompositionContextFactory(
            profileReader, canonicalReader, memoryReader, previousSceneReader,
            NullLogger<SceneCompositionContextFactory>.Instance
        );

        var context = await factory.CreateContextAsync(
            characterId: charId,
            sessionId: sessionId,
            turnId: turnId,
            sceneRevision: 2,
            locationContext: "Library"
        );

        // 6. Assertions
        Assert.NotNull(context);
        Assert.Equal(charId, context.CharacterId);
        Assert.Equal(profile.EyeColor, context.CharacterVisualProfile?.EyeColor);
        Assert.Equal(canonicalRef.ReferenceUrl, context.CanonicalVisualReference?.ReferenceUrl);
        Assert.NotNull(context.PreviousScene);

        // Invariant: Only Valid (Current/Historical) memories are loaded. Quarantined and Deleted are excluded.
        Assert.Single(context.RelevantVisualMemories);
        Assert.Equal(validImageId, context.RelevantVisualMemories[0].ArtifactId);
    }
}
