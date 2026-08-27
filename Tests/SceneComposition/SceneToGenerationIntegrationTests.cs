using Application.DTOs;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Infrastructure.Services.Scene;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.SceneComposition;

public sealed class SceneToGenerationIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public SceneToGenerationIntegrationTests()
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
    public async Task FullFlow_ChatIntent_ToSceneComposition_ToGenerationRequest_Succeeds()
    {
        await using var db = new ProjectDbContext(_options);
        var dateTimeProvider = new SystemDateTimeProvider();

        // 1. Setup Services & Readers
        var profileService = new CharacterVisualProfileService(db, NullLogger<CharacterVisualProfileService>.Instance);
        var referenceService = new CharacterVisualReferenceService(db, profileService, NullLogger<CharacterVisualReferenceService>.Instance);
        var profileReader = new CharacterVisualProfileReader(db);
        var memoryReader = new VisualMemoryReader(db);
        var canonicalReader = new CanonicalReferenceReader(db);
        var previousSceneReader = new PreviousSceneReader(db);

        var composer = new SceneComposer(NullLogger<SceneComposer>.Instance);
        var visualContextResolver = new VisualContextResolver(NullLogger<VisualContextResolver>.Instance);
        var promptComposer = new ScenePromptComposer();
        var requestMapper = new SceneGenerationRequestMapper();

        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        // 2. Seed Character Visual Profile and Canonical Reference
        var profile = await profileService.CreateProfileAsync(
            characterId: charId,
            eyeColor: "Crimson Red",
            hairColor: "Silver",
            skinTone: "Pale",
            bodyIdentity: "Slender athletic",
            currentOutfit: "Battle Armor"
        );

        var canonicalRef = await referenceService.RegisterReferenceAsync(new RegisterVisualReferenceRequest(
            CharacterId: charId,
            ReferenceUrl: "https://cdn.project00.ai/valerius_canonical.png",
            IsCanonical: true,
            Type: VisualReferenceType.Canonical
        ));

        // 3. User / Chat Turn produces SceneIntent
        var intent = new SceneIntent(
            characterId: charId,
            locationHint: "Gothic Library",
            actionHint: "Reading an ancient scroll",
            sessionId: sessionId,
            turnId: turnId
        );

        // 4. Build SceneCompositionContext via Application Readers
        var loadedProfile = await profileReader.GetProfileByCharacterIdAsync(charId);
        var loadedCanonical = await canonicalReader.GetActiveCanonicalReferenceAsync(charId);
        var loadedMemories = await memoryReader.GetRelevantMemoriesAsync(charId, intent.LocationHint);
        var previousScene = await previousSceneReader.GetLatestSceneBySessionAsync(sessionId);

        var compContext = new SceneCompositionContext(
            CharacterId: charId,
            SessionId: sessionId,
            TurnId: turnId,
            SceneRevision: 1,
            PreviousScene: previousScene,
            CharacterVisualProfile: loadedProfile,
            CanonicalVisualReference: loadedCanonical,
            RelevantVisualMemories: loadedMemories
        );

        // 5. Scene Composition
        var sceneSpec = await composer.ComposeAsync(intent, compContext);
        Assert.NotNull(sceneSpec);
        Assert.Equal("Gothic Library", sceneSpec.Location);
        Assert.Equal("Reading an ancient scroll", sceneSpec.Action);

        // Persist SceneSpecification
        db.SceneSpecifications.Add(sceneSpec);
        await db.SaveChangesAsync();

        // 6. Visual Context Resolution
        var visualContext = await visualContextResolver.ResolveVisualContextAsync(charId, sceneSpec, compContext);
        Assert.NotNull(visualContext.CanonicalIdentityReference);
        Assert.Equal(canonicalRef.ReferenceUrl, visualContext.CanonicalIdentityReference.ReferenceUrl);

        // 7. Prompt Composition & Generation Request Mapping
        var genProfile = new GenerationProfile(
            Seed: 12345L,
            Workflow: "VisualIdentity",
            WorkflowVersion: 1,
            Width: 1024,
            Height: 1024
        );

        var snapshot = requestMapper.MapToVisualSnapshot(sceneSpec, visualContext, genProfile, promptComposer);

        // Assert: Valid snapshot ready for PR22–30 Generation Engine
        Assert.NotNull(snapshot);
        Assert.Equal(turnId, snapshot.TurnId);
        Assert.Equal(sessionId, snapshot.SessionId);
        Assert.Equal(charId, snapshot.CharacterId);
        Assert.Equal(1, snapshot.SceneRevision);
        Assert.Equal(canonicalRef.ReferenceUrl, snapshot.IdentityReferenceUrl);
        Assert.Contains("[Character:", snapshot.SceneDescription?.EnglishPromptTags.FirstOrDefault());
        Assert.Contains("Silver hair, Crimson Red eyes", snapshot.SceneDescription?.EnglishPromptTags.FirstOrDefault());

        // 8. Verify DB State: Profile and Canonical References remain unmutated by scene composition (Strict Identity Isolation)
        var profileAfter = await db.CharacterVisualProfiles.FirstAsync(p => p.CharacterId == charId);
        Assert.Equal("Crimson Red", profileAfter.EyeColor);
        Assert.Equal("Silver", profileAfter.HairColor);
        Assert.Equal(2, profileAfter.VisualVersion);
    }
}
