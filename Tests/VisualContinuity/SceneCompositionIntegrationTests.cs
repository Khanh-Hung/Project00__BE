using Application.Common;
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

namespace Tests.VisualContinuity;

public sealed class SceneCompositionIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CoreDbContext> _options;

    public SceneCompositionIntegrationTests()
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
            return Task.FromResult("https://cdn.project00.ai/generated_continuity_scene.png");
        }

        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult("https://cdn.project00.ai/generated_continuity_scene.png");
        }
    }

    [Fact]
    public async Task CompleteFlow_ContinuityResolver_FeedsCompositionPipeline_AndProducesAcceptedArtifact()
    {
        await using var db = new CoreDbContext(_options);
        var dateTimeProvider = new SystemDateTimeProvider();

        // 1. Setup Architecture Services
        var profileService = new CharacterVisualProfileService(db, NullLogger<CharacterVisualProfileService>.Instance);
        var referenceService = new CharacterVisualReferenceService(db, profileService, NullLogger<CharacterVisualReferenceService>.Instance);

        var pipelineService = SceneCompositionTestHelper.CreatePipeline(db);

        var unitOfWork = new UnitOfWork(db);
        var visualStateResolver = new VisualStateResolver(
            unitOfWork,
            sceneStateTracker: null,
            sceneCompositionPipeline: pipelineService,
            logger: NullLogger<VisualStateResolver>.Instance
        );

        var lyraId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // 2. Seed Character Visual Profile
        await profileService.CreateProfileAsync(
            characterId: lyraId,
            eyeColor: "Deep Violet",
            hairColor: "Silver Lilac",
            skinTone: "Porcelain",
            bodyIdentity: "Slender scholar",
            currentOutfit: "Emerald Academic Robes"
        );

        var canonicalRef = await referenceService.RegisterReferenceAsync(new RegisterVisualReferenceRequest(
            CharacterId: lyraId,
            ReferenceUrl: "https://cdn.project00.ai/canonical_lyra_face.png",
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
        var (sceneState, transientState, visualSnapshot) = await visualStateResolver.ResolveTurnVisualStateAsync(
            character: character,
            session: session,
            userMessage: "Lyra ngồi đọc sách trong thư viện.",
            assistantReply: "Tôi đang lật giở từng trang sách cổ.",
            currentMood: CharacterMood.Curious,
            turnId: turnId
        );

        Assert.NotNull(visualSnapshot);
        Assert.Equal(lyraId, visualSnapshot.CharacterId);
        Assert.Equal(1, visualSnapshot.SceneRevision);
        Assert.Equal("Emerald Academic Robes", visualSnapshot.VisualIdentity?.ClothingStyle);

        // 4. Feed into ImageGenerationJobHandler
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
            workerId: "test-worker-pr32",
            now: DateTime.UtcNow
        );

        // Assert: Generation finished successfully
        Assert.Equal(JobExecutionStatus.Completed, jobExecutionResult.Status);
        Assert.Equal(1, imageService.CallCount);

        // Verify DB State
        var job = await db.ImageGenerationJobs.AsNoTracking().FirstOrDefaultAsync(j => j.GenerationRequestId == generationRequestId);
        Assert.NotNull(job);
        Assert.Equal(ImageJobStatus.Completed, job.Status);

        var createdImage = await db.SceneImages.AsNoTracking().FirstOrDefaultAsync(img => img.GenerationRequestId == generationRequestId);
        Assert.NotNull(createdImage);
        Assert.True(createdImage.IsCurrent);
        Assert.Equal(ArtifactLifecycleStatus.Current, createdImage.LifecycleStatus);
    }
}
