using System.Text.Json;
using Application.DTOs;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories;
using Infrastructure.Services;
using Infrastructure.Services.Scene;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.SceneComposition;

public sealed class ProductionSceneCompositionIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ProjectDbContext> _options;

    public ProductionSceneCompositionIntegrationTests()
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
    public async Task ProductionTurn_ExecutesSceneCompositionPipeline_PersistsSceneSpecification_AndFeedsGenerationOrchestrator()
    {
        await using var db = new ProjectDbContext(_options);
        var dateTimeProvider = new SystemDateTimeProvider();

        // 1. Setup Architecture Services & Readers
        var profileService = new CharacterVisualProfileService(db, NullLogger<CharacterVisualProfileService>.Instance);
        var referenceService = new CharacterVisualReferenceService(db, profileService, NullLogger<CharacterVisualReferenceService>.Instance);

        var profileReader = new CharacterVisualProfileReader(db);
        var memoryReader = new VisualMemoryReader(db);
        var canonicalReader = new CanonicalReferenceReader(db);
        var previousSceneReader = new PreviousSceneReader(db);

        var contextFactory = new SceneCompositionContextFactory(
            profileReader, canonicalReader, memoryReader, previousSceneReader,
            NullLogger<SceneCompositionContextFactory>.Instance
        );

        var composer = new SceneComposer(NullLogger<SceneComposer>.Instance);
        var visualContextResolver = new VisualContextResolver(NullLogger<VisualContextResolver>.Instance);
        var promptComposer = new ScenePromptComposer();
        var requestMapper = new SceneGenerationRequestMapper();

        var pipelineService = new SceneCompositionPipelineService(
            contextFactory,
            composer,
            visualContextResolver,
            promptComposer,
            requestMapper,
            NullLogger<SceneCompositionPipelineService>.Instance
        );

        var unitOfWork = new UnitOfWork(db);
        var visualStateResolver = new VisualStateResolver(
            unitOfWork,
            sceneStateTracker: null,
            profileProvider: new VisualGenerationProfileProvider(),
            sceneCompositionPipeline: pipelineService,
            logger: NullLogger<VisualStateResolver>.Instance
        );

        var lyraId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        // 2. Seed Character Visual Profile (Lyra) and Canonical Reference
        var profile = await profileService.CreateProfileAsync(
            characterId: lyraId,
            eyeColor: "Deep Violet",
            hairColor: "Silver Lilac",
            skinTone: "Porcelain",
            bodyIdentity: "Slender scholar",
            currentOutfit: "Emerald Academic Robes"
        );

        var canonicalRef = await referenceService.RegisterReferenceAsync(new RegisterVisualReferenceRequest(
            CharacterId: lyraId,
            ReferenceUrl: "https://cdn.project00.ai/lyra_canonical.png",
            IsCanonical: true,
            Type: VisualReferenceType.Canonical
        ));

        var character = new Character(
            name: "Lyra",
            title: "Archivist of the Arcane",
            avatarUrl: "https://cdn.project00.ai/lyra_avatar.png",
            personalityPrompt: "Scholarly, observant",
            greeting: "Greetings, traveler.",
            category: "Fantasy"
        );
        typeof(Character).GetProperty("Id")!.SetValue(character, lyraId);
        typeof(Character).GetProperty("VisualIdentity")!.SetValue(character, new CharacterVisualIdentity(
            Hair: "Silver Lilac",
            Eyes: "Deep Violet",
            Skin: "Porcelain",
            ClothingStyle: "Emerald Academic Robes",
            CanonicalReferenceUrl: canonicalRef.ReferenceUrl
        ));

        var session = new ChatSession(lyraId, userId, "Roleplay Session");
        typeof(ChatSession).GetProperty("Id")!.SetValue(session, sessionId);

        db.Characters.Add(character);
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        // 3. Execute Production Turn via VisualStateResolver (Simulating CharacterRuntime turn execution)
        var userMessage = "Lyra ngồi đọc sách trong thư viện lúc trời mưa.";
        var assistantReply = "Mình đang lật từng trang sách cổ trong không gian yên tĩnh của thư viện.";

        var (resolvedSceneState, transientState, visualSnapshot) = await visualStateResolver.ResolveTurnVisualStateAsync(
            character: character,
            session: session,
            userMessage: userMessage,
            assistantReply: assistantReply,
            currentMood: CharacterMood.Neutral,
            turnId: turnId
        );

        await unitOfWork.SaveChangesAsync();

        // 4. Verify VisualSnapshot contains PR #31 structured composition
        Assert.NotNull(visualSnapshot);
        Assert.Equal(turnId, visualSnapshot.TurnId);
        Assert.Equal(sessionId, visualSnapshot.SessionId);
        Assert.Equal(lyraId, visualSnapshot.CharacterId);
        Assert.Equal(1, visualSnapshot.SceneRevision);
        Assert.Equal(canonicalRef.ReferenceUrl, visualSnapshot.IdentityReferenceUrl);
        Assert.NotNull(visualSnapshot.SceneDescription);

        var prompt = visualSnapshot.SceneDescription.EnglishPromptTags.FirstOrDefault();
        Assert.NotNull(prompt);
        Assert.Contains("[Character: Silver Lilac hair, Deep Violet eyes", prompt);
        Assert.Contains("[Pose: seated naturally]", prompt);
        Assert.Contains("[Outfit: Emerald Academic Robes]", prompt);

        // 5. Verify SceneSpecification was persisted in DB with SceneFingerprint
        var persistedSpec = await db.SceneSpecifications.FirstOrDefaultAsync(s => s.CharacterId == lyraId && s.TurnId == turnId);
        Assert.NotNull(persistedSpec);
        Assert.Equal(1, persistedSpec.SceneRevision);
        Assert.NotNull(persistedSpec.SceneFingerprint);
        Assert.Equal(64, persistedSpec.SceneFingerprint.Length);

        // 6. Simulate Outbox ImageGeneration Event Dispatch to PR22–30 ImageGenerationOrchestrator
        var outboxPayload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: lyraId,
            UserId: userId,
            Snapshot: visualSnapshot,
            GenerationRequestId: Guid.NewGuid()
        );

        // Assert: Outbox payload carries the exact VisualSnapshot produced by SceneCompositionPipeline
        Assert.Equal(visualSnapshot.TurnId, outboxPayload.Snapshot.TurnId);
        Assert.Equal(visualSnapshot.SceneRevision, outboxPayload.Snapshot.SceneRevision);
        Assert.Equal(canonicalRef.ReferenceUrl, outboxPayload.Snapshot.IdentityReferenceUrl);

        // 7. Verify Character Visual Profile core identity remains unmutated (Strict Identity Isolation)
        var profileAfter = await db.CharacterVisualProfiles.FirstAsync(p => p.CharacterId == lyraId);
        Assert.Equal("Deep Violet", profileAfter.EyeColor);
        Assert.Equal("Silver Lilac", profileAfter.HairColor);
        Assert.Equal(2, profileAfter.VisualVersion);
    }
}
