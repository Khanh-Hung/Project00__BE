using System.Text.Json;
using Application.Common;
using Application.Common.Exceptions;
using Application.DTOs;
using Application.Enums;
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
    private readonly DbContextOptions<CoreDbContext> _options;

    public ProductionSceneCompositionIntegrationTests()
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

    private sealed class FakeImageService : IImageGenerationService
    {
        public int CallCount { get; private set; }

        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult("https://cdn.project00.ai/generated_scene_lyra.png");
        }

        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult("https://cdn.project00.ai/generated_scene_lyra.png");
        }
    }

    [Fact]
    public async Task ProductionTurn_ExecutesSceneCompositionPipeline_FeedsGenerationOrchestrator_AndProducesAcceptedArtifact()
    {
        await using var db = new CoreDbContext(_options);
        var dateTimeProvider = new SystemDateTimeProvider();

        // 1. Setup Architecture Services & Readers
        var profileService = new CharacterVisualProfileService(db, NullLogger<CharacterVisualProfileService>.Instance);
        var referenceService = new CharacterVisualReferenceService(db, profileService, NullLogger<CharacterVisualReferenceService>.Instance);

        var pipelineService = SceneCompositionTestHelper.CreatePipeline(db);

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

        // 3. Execute Production Turn via VisualStateResolver
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

        // 6. Execute REAL ImageGenerationJobHandler / ImageGenerationOrchestrator Boundary
        var imageService = new FakeImageService();
        var promptCompiler = new VisualPromptCompiler();
        var qualityEvaluator = new DevelopmentPassThroughIdentityQualityEvaluator();
        var qualityGuardPolicy = new IdentityQualityGuardPolicy(MinAcceptableIdentitySimilarity: 0.75f, MaxAttempts: 3);

        var generationHandler = new ImageGenerationJobHandler(
            dbContext: db,
            visualCompiler: promptCompiler,
            imageService: imageService,
            logger: NullLogger<ImageGenerationJobHandler>.Instance,
            dateTimeProvider: dateTimeProvider,
            qualityEvaluator: qualityEvaluator,
            qualityGuardPolicy: qualityGuardPolicy
        );

        var generationRequestId = Guid.NewGuid();
        var outboxPayload = new SceneImageGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: lyraId,
            UserId: userId,
            Snapshot: visualSnapshot,
            GenerationRequestId: generationRequestId
        );

        var jobExecutionResult = await generationHandler.HandleSceneImageGenerationAsync(
            payload: outboxPayload,
            outboxId: Guid.NewGuid(),
            workerId: "test-worker-1",
            now: DateTime.UtcNow
        );

        // Assert: Orchestrator completed generation successfully
        Assert.Equal(JobExecutionStatus.Completed, jobExecutionResult.Status);
        Assert.Equal(1, imageService.CallCount);

        // 7. Verify DB State: ImageGenerationJob and SceneImage Artifact created and accepted
        var job = await db.ImageGenerationJobs.AsNoTracking().FirstOrDefaultAsync(j => j.GenerationRequestId == generationRequestId);
        Assert.NotNull(job);
        Assert.Equal(ImageJobStatus.Completed, job.Status);
        Assert.Equal(1, job.SceneRevision);
        Assert.NotNull(job.AcceptedAttemptId);

        var acceptedAttempt = await db.ImageGenerationAttempts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == job.AcceptedAttemptId);
        Assert.NotNull(acceptedAttempt);
        Assert.Equal(GenerationAttemptStatus.Succeeded, acceptedAttempt.Status);

        var createdImage = await db.SceneImages.AsNoTracking().FirstOrDefaultAsync(img => img.GenerationRequestId == generationRequestId);
        Assert.NotNull(createdImage);
        Assert.True(createdImage.IsCurrent);
        Assert.Equal(1, createdImage.SceneRevision);
        Assert.Equal(canonicalRef.ReferenceUrl, createdImage.IdentityReferenceUrl);
        Assert.Equal(ArtifactLifecycleStatus.Current, createdImage.LifecycleStatus);

        // 8. Verify Character Visual Profile core identity remains unmutated (Strict Identity Isolation)
        var profileAfter = await db.CharacterVisualProfiles.AsNoTracking().FirstAsync(p => p.CharacterId == lyraId);
        Assert.Equal("Deep Violet", profileAfter.EyeColor);
        Assert.Equal("Silver Lilac", profileAfter.HairColor);
        Assert.Equal(2, profileAfter.VisualVersion);
    }

    [Fact]
    public async Task SceneCompositionFailure_ThrowsAndAbortsGeneration_DoesNotSilentlyFallbackToLegacySnapshot()
    {
        await using var db = new CoreDbContext(_options);
        var unitOfWork = new UnitOfWork(db);

        var lyraId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        // Create failing pipeline mock to simulate context resolution or validation failure
        var failingPipeline = new FailingSceneCompositionPipeline();

        var visualStateResolver = new VisualStateResolver(
            unitOfWork,
            sceneStateTracker: null,
            profileProvider: new VisualGenerationProfileProvider(),
            sceneCompositionPipeline: failingPipeline,
            logger: NullLogger<VisualStateResolver>.Instance
        );

        var character = new Character(
            name: "Lyra",
            title: "Archivist",
            avatarUrl: "https://cdn.project00.ai/avatar.png",
            personalityPrompt: "Scholarly",
            greeting: "Hello",
            category: "Fantasy"
        );
        typeof(Character).GetProperty("Id")!.SetValue(character, lyraId);

        var session = new ChatSession(lyraId, userId, "Session");
        typeof(ChatSession).GetProperty("Id")!.SetValue(session, sessionId);

        // Act & Assert: VisualStateResolver MUST throw typed SceneCompositionException and FAIL FAST.
        // It must NEVER silently catch the error and generate an unconditioned legacy snapshot.
        var ex = await Assert.ThrowsAsync<SceneCompositionException>(() =>
            visualStateResolver.ResolveTurnVisualStateAsync(
                character: character,
                session: session,
                userMessage: "User prompt",
                assistantReply: "Reply",
                currentMood: CharacterMood.Neutral,
                turnId: turnId
            )
        );

        Assert.Equal(SceneCompositionFailureCategory.ContextResolutionFailure, ex.FailureCategory);

        // Verify that NO SceneSpecification was saved to DB
        var specInDb = await db.SceneSpecifications.FirstOrDefaultAsync(s => s.TurnId == turnId);
        Assert.Null(specInDb);
    }

    private sealed class FailingSceneCompositionPipeline : ISceneCompositionPipelineService
    {
        public Task<SceneCompositionPipelineResult> ExecuteAsync(
            SceneIntent intent,
            GenerationProfile generationProfile,
            int sceneRevision = 1,
            CancellationToken ct = default)
        {
            throw new SceneCompositionException(
                SceneCompositionFailureCategory.ContextResolutionFailure,
                "Simulated authoritative context resolution failure.");
        }
    }
}
