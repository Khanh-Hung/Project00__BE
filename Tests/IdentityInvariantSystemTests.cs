using System.Text.Json;
using Application.Abstractions.Data;
using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Application.Services;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests;

public sealed class IdentityInvariantSystemTests
{
    private readonly VisualPromptCompiler _compiler = new();

    [Fact]
    public void CompileNegativePrompt_WhenGenderIsMale_SuppressesFemaleTokensWithoutRestrictingClothing()
    {
        var identity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Male,
            Hair: "short black hair",
            Eyes: "amber eyes"
        );

        var negative = _compiler.CompileNegativePrompt(identity);

        Assert.Contains("1girl", negative);
        Assert.Contains("female", negative);
        Assert.Contains("woman", negative);
        Assert.Contains("breasts", negative);
        Assert.Contains("feminine face", negative);
        Assert.DoesNotContain("1man", negative);
        Assert.DoesNotContain("dress", negative);
        Assert.DoesNotContain("skirt", negative);
    }

    [Fact]
    public void CompileNegativePrompt_WhenGenderIsFemale_SuppressesMaleTokens()
    {
        var identity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Female,
            Hair: "long silver hair",
            Eyes: "crimson eyes"
        );

        var negative = _compiler.CompileNegativePrompt(identity);

        Assert.Contains("1man", negative);
        Assert.Contains("male", negative);
        Assert.Contains("boy", negative);
        Assert.Contains("masculine face", negative);
        Assert.DoesNotContain("1girl", negative);
    }

    [Fact]
    public void CompileScenePrompt_WhenGenderIsNonBinary_InjectsNonBinaryPositiveTokens()
    {
        var identity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.NonBinary,
            Hair: "medium teal hair",
            Eyes: "violet eyes"
        );

        var snapshot = VisualSnapshot.Create(
            turnId: Guid.NewGuid(),
            sessionId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            sceneRevision: 1,
            visualIdentity: identity,
            sceneState: new SessionSceneState("Garden", "Center", "Robes", "Day", null, "Quiet", 1, DateTime.UtcNow),
            transientState: null,
            generationProfile: GenerationProfile.CreateDefault()
        );

        var positive = _compiler.CompileScenePrompt(snapshot);
        var negative = _compiler.CompileNegativePrompt(snapshot);

        Assert.Contains("non-binary", positive);
        Assert.Contains("androgynous appearance", positive);
        Assert.DoesNotContain("1girl, female, feminine face", positive);
        Assert.DoesNotContain("1girl, anime girl, female", negative);
        Assert.DoesNotContain("1man, anime man, male", negative);
    }

    [Fact]
    public void CompileScenePrompt_WhenGenderIsMale_InjectsMasculinePositiveTokens()
    {
        var identity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Male,
            Hair: "short black hair",
            Eyes: "amber eyes",
            ClothingStyle: "dark knight plate armor"
        );

        var snapshot = VisualSnapshot.Create(
            turnId: Guid.NewGuid(),
            sessionId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            sceneRevision: 1,
            visualIdentity: identity,
            sceneState: new SessionSceneState("Armory", "Center", "Armor", "Day", null, "Quiet", 1, DateTime.UtcNow),
            transientState: null,
            generationProfile: GenerationProfile.CreateDefault()
        );

        var positive = _compiler.CompileScenePrompt(snapshot);

        Assert.Contains("1man", positive);
        Assert.Contains("masculine face", positive);
        Assert.DoesNotContain("1girl", positive);
    }

    [Fact]
    public void CompileScenePrompt_WithCriticalSignatureFeatures_InjectsFeaturesEveryTurn()
    {
        var hornFeature = new SignatureFeature(
            Name: "DragonHorns",
            PositiveTokens: "sharp obsidian dragon horns with glowing red tips",
            NegativeTokens: "deformed horns, missing horns, extra horns",
            Importance: FeatureImportance.Critical,
            Persistence: FeaturePersistence.EveryTurn
        );

        var identity = new CharacterVisualIdentity(
            Presentation: GenderPresentation.Female,
            Hair: "long pure silver hair",
            Eyes: "glowing red eyes",
            SignatureFeatures: new[] { hornFeature }
        );

        var snapshot = VisualSnapshot.Create(
            turnId: Guid.NewGuid(),
            sessionId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            sceneRevision: 5,
            visualIdentity: identity,
            sceneState: new SessionSceneState("Grand Hall", "Throne", "Robes", "Night", null, "Solemn", 5, DateTime.UtcNow),
            transientState: null,
            generationProfile: GenerationProfile.CreateDefault()
        );

        var positive = _compiler.CompileScenePrompt(snapshot);
        var negative = _compiler.CompileNegativePrompt(snapshot);

        Assert.Contains("sharp obsidian dragon horns with glowing red tips", positive);
        Assert.Contains("deformed horns", negative);
        Assert.Contains("missing horns", negative);
    }

    [Theory]
    [InlineData(FeaturePersistence.EveryTurn, FeatureImportance.Critical, Slot2Context.ColdStart, true)]
    [InlineData(FeaturePersistence.EveryTurn, FeatureImportance.Critical, Slot2Context.SameScene, true)]
    [InlineData(FeaturePersistence.EveryTurn, FeatureImportance.Critical, Slot2Context.SceneTransition, true)]
    [InlineData(FeaturePersistence.EveryTurn, FeatureImportance.Contextual, Slot2Context.SceneTransition, true)]
    [InlineData(FeaturePersistence.SameSceneOnly, FeatureImportance.Critical, Slot2Context.SameScene, true)]
    [InlineData(FeaturePersistence.SameSceneOnly, FeatureImportance.Critical, Slot2Context.SceneTransition, false)]
    [InlineData(FeaturePersistence.SameSceneOnly, FeatureImportance.Contextual, Slot2Context.SameScene, true)]
    [InlineData(FeaturePersistence.SameSceneOnly, FeatureImportance.Contextual, Slot2Context.SceneTransition, false)]
    public void SignatureFeature_ShouldInject_AdheresToExplicitContextSemantics(
        FeaturePersistence persistence,
        FeatureImportance importance,
        Slot2Context context,
        bool expectedShouldInject)
    {
        var feature = new SignatureFeature(
            Name: "TestFeature",
            PositiveTokens: "test feature tokens",
            Importance: importance,
            Persistence: persistence
        );

        var result = feature.ShouldInject(context);

        Assert.Equal(expectedShouldInject, result);
    }

    [Theory]
    [InlineData(true, false, 0.0, 0.0, false)]
    [InlineData(false, false, 0.15, 0.30, true)]
    [InlineData(false, true, 0.08, 0.20, true)]
    public void Slot2ConditioningPolicy_ResolvesExpectedParameters(
        bool isColdStart,
        bool isTransition,
        double expectedWeight,
        double expectedEndAt,
        bool expectedActive)
    {
        var policy = new Slot2ConditioningPolicy(
            SameSceneWeight: 0.15,
            SameSceneEndAt: 0.30,
            TransitionWeight: 0.08,
            TransitionEndAt: 0.20,
            BypassOnColdStart: true
        );

        var decision = policy.Decide(isColdStart, isTransition);

        Assert.Equal(expectedWeight, decision.Weight, precision: 4);
        Assert.Equal(expectedEndAt, decision.EndAt, precision: 4);
        Assert.Equal(expectedActive, decision.IsActive);
    }

    [Theory]
    [InlineData("AiProviders:ImageGeneration:Slot2Policy:SameSceneWeight", "invalid_number")]
    [InlineData("AiProviders:ImageGeneration:Slot2Policy:SameSceneWeight", "1.5")]
    [InlineData("AiProviders:ImageGeneration:Slot2Policy:SameSceneWeight", "-0.1")]
    [InlineData("AiProviders:ImageGeneration:Slot2Policy:TransitionWeight", "NaN")]
    [InlineData("AiProviders:ImageGeneration:Slot2Policy:BypassOnColdStart", "not_a_bool")]
    public void Slot2ConditioningPolicy_FromConfiguration_ThrowsOnInvalidConfiguration(string key, string invalidValue)
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationManager();
        config[key] = invalidValue;

        Assert.Throws<InvalidOperationException>(() => Slot2ConditioningPolicy.FromConfiguration(config));
    }

    [Fact]
    public async Task VisualStateResolver_MultiTurnPipeline_GuaranteesCanonicalReferenceImmutability_And_DynamicSlot2Resolution()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ProjectDbContext(options);
        var unitOfWork = new UnitOfWork(db);

        var canonicalAvatarUrl = "https://cdn.project00.ai/avatars/valerius_canonical_face.png";
        var hornFeature = new SignatureFeature(
            Name: "ShadowInsignia",
            PositiveTokens: "obsidian knight pauldron crest",
            Importance: FeatureImportance.Critical,
            Persistence: FeaturePersistence.EveryTurn
        );

        var visualIdentity = new CharacterVisualIdentity(
            CanonicalReferenceUrl: canonicalAvatarUrl,
            Presentation: GenderPresentation.Male,
            Hair: "black hair",
            Eyes: "amber eyes",
            SignatureFeatures: new[] { hornFeature }
        );

        var character = new Character(
            name: "Valerius",
            title: "Shadow Knight",
            avatarUrl: canonicalAvatarUrl,
            personalityPrompt: "Noble commander",
            greeting: "Greetings",
            category: "Knight",
            visualIdentity: visualIdentity
        );
        await db.Characters.AddAsync(character);

        var session = new ChatSession(character.Id, Guid.NewGuid(), "Roleplay Session");
        await db.ChatSessions.AddAsync(session);
        await db.SaveChangesAsync();

        var stateTracker = new DummySceneStateTracker();
        var profileProvider = new VisualGenerationProfileProvider();
        var resolver = new VisualStateResolver(unitOfWork, stateTracker, profileProvider);

        // Turn 1: Cold Start in Armory
        stateTracker.NextDelta = new SceneStateDelta(LocationChange: "Armory");
        var (s1, t1, snap1) = await resolver.ResolveTurnVisualStateAsync(
            character, session, "Hello", "Welcome to the armory.", CharacterMood.Neutral, Guid.NewGuid());

        Assert.Equal(canonicalAvatarUrl, snap1.IdentityReferenceUrl);
        Assert.Null(snap1.PreviousSceneImageUrl);
        Assert.Equal(1, snap1.SceneRevision);

        // Commit Turn 1 Image into DB
        var turn1Image = new SceneImage(session.Id, character.Id, snap1.TurnId, 1, "https://cdn.project00.ai/scenes/valerius_turn_1.png", "Prompt 1", isCurrent: true);
        await db.SceneImages.AddAsync(turn1Image);
        await db.SaveChangesAsync();

        // Turn 2: Same-scene in Armory
        stateTracker.NextDelta = new SceneStateDelta(LocationChange: "Armory", ActionChange: "polishing armor");
        var (s2, t2, snap2) = await resolver.ResolveTurnVisualStateAsync(
            character, session, "Check armor", "I will polish it.", CharacterMood.Neutral, Guid.NewGuid());

        // Assert: IdentityReferenceUrl is ALWAYS the canonical avatar, NOT turn 1 image!
        Assert.Equal(canonicalAvatarUrl, snap2.IdentityReferenceUrl);
        Assert.Equal("https://cdn.project00.ai/scenes/valerius_turn_1.png", snap2.PreviousSceneImageUrl);
        Assert.Equal(2, snap2.SceneRevision);

        // Verify Same-Scene parameters: weight = 0.15, endAt = 0.30
        using (var doc = JsonDocument.Parse(snap2.GenerationProfile.ParametersJson))
        {
            var sc = doc.RootElement.GetProperty("sceneContinuity");
            Assert.Equal(0.15, sc.GetProperty("weight").GetDouble(), precision: 2);
            Assert.Equal(0.30, sc.GetProperty("endAt").GetDouble(), precision: 2);
        }

        // Commit Turn 2 Image into DB
        turn1Image.SetCurrent(false);
        var turn2Image = new SceneImage(session.Id, character.Id, snap2.TurnId, 2, "https://cdn.project00.ai/scenes/valerius_turn_2.png", "Prompt 2", isCurrent: true);
        await db.SceneImages.AddAsync(turn2Image);
        await db.SaveChangesAsync();

        // Turn 3: Scene Transition to War Room
        stateTracker.NextDelta = new SceneStateDelta(LocationChange: "War Room", ActionChange: "studying map");
        var (s3, t3, snap3) = await resolver.ResolveTurnVisualStateAsync(
            character, session, "Move to war room", "Let us study the map.", CharacterMood.Neutral, Guid.NewGuid());

        // Assert: IdentityReferenceUrl is STILL the canonical avatar! PreviousSceneImageUrl is turn 2 image.
        Assert.Equal(canonicalAvatarUrl, snap3.IdentityReferenceUrl);
        Assert.Equal("https://cdn.project00.ai/scenes/valerius_turn_2.png", snap3.PreviousSceneImageUrl);
        Assert.Equal(3, snap3.SceneRevision);

        // Verify Scene Transition parameters: attenuated weight = 0.08, endAt = 0.20
        using (var doc = JsonDocument.Parse(snap3.GenerationProfile.ParametersJson))
        {
            var sc = doc.RootElement.GetProperty("sceneContinuity");
            Assert.Equal(0.08, sc.GetProperty("weight").GetDouble(), precision: 2);
            Assert.Equal(0.20, sc.GetProperty("endAt").GetDouble(), precision: 2);
        }
    }

    [Fact]
    public async Task ImageGenerationJobHandler_EndToEnd_DispatchesRequest_WithCompiledInvariants_And_ImmutableReference()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new ProjectDbContext(options);
        var unitOfWork = new UnitOfWork(db);

        var canonicalAvatar = "https://cdn.project00.ai/avatars/valerius_canonical_face.png";
        var crestFeature = new SignatureFeature(
            Name: "ShadowCrest",
            PositiveTokens: "obsidian knight pauldron crest",
            NegativeTokens: "deformed crest, missing crest",
            Importance: FeatureImportance.Critical,
            Persistence: FeaturePersistence.EveryTurn
        );

        var identity = new CharacterVisualIdentity(
            CanonicalReferenceUrl: canonicalAvatar,
            Presentation: GenderPresentation.Male,
            Hair: "short black hair",
            Eyes: "golden amber eyes",
            ClothingStyle: "dark plate armor",
            SignatureFeatures: new[] { crestFeature }
        );

        var character = new Character(
            name: "Valerius",
            title: "Commander",
            avatarUrl: canonicalAvatar,
            personalityPrompt: "Loyal",
            greeting: "Hail",
            category: "Knight",
            visualIdentity: identity
        );
        await db.Characters.AddAsync(character);

        var userId = Guid.NewGuid();
        var session = new ChatSession(character.Id, userId, "Roleplay Session");
        session.UpdateSceneState(new SessionSceneState("Armory", "Center", "Armor", "Day", null, "Quiet", 1, DateTime.UtcNow));
        await db.ChatSessions.AddAsync(session);

        // Turn 1 generated image in DB
        var turn1Image = new SceneImage(session.Id, character.Id, Guid.NewGuid(), 1, "https://cdn.project00.ai/scenes/valerius_t1.png", "Prompt 1", isCurrent: true);
        await db.SceneImages.AddAsync(turn1Image);
        await db.SaveChangesAsync();

        var stateTracker = new DummySceneStateTracker();
        var profileProvider = new VisualGenerationProfileProvider();
        var resolver = new VisualStateResolver(unitOfWork, stateTracker, profileProvider);

        // Resolve Turn 2 (Same-Scene in Armory)
        stateTracker.NextDelta = new SceneStateDelta(LocationChange: "Armory", ActionChange: "cleaning broadsword");
        var (sceneState, transientState, snapshot) = await resolver.ResolveTurnVisualStateAsync(
            character, session, "Inspect weapon", "I am sharpening the blade.", CharacterMood.Neutral, Guid.NewGuid());

        var generationRequestId = Guid.NewGuid();
        var job = new ImageGenerationJob(session.Id, snapshot.TurnId, character.Id, snapshot.SceneRevision, generationRequestId);
        await db.ImageGenerationJobs.AddAsync(job);
        await db.SaveChangesAsync();

        var capturingService = new CapturingImageGenerationService();
        var compiler = new VisualPromptCompiler();
        var dateTimeProvider = new SystemDateTimeProvider();
        var logger = NullLogger<ImageGenerationJobHandler>.Instance;

        var handler = new ImageGenerationJobHandler(db, compiler, capturingService, logger, dateTimeProvider);

        var payload = new SceneImageGenerationOutboxPayload(snapshot.TurnId, character.Id, userId, snapshot, generationRequestId);

        // Act: Process the generation job end-to-end
        var result = await handler.HandleSceneImageGenerationAsync(payload, Guid.NewGuid(), "worker-1", DateTime.UtcNow);

        // Assert: Job executed successfully
        Assert.Equal(JobExecutionStatus.Completed, result.Status);
        Assert.NotNull(capturingService.CapturedRequest);

        var req = capturingService.CapturedRequest;

        // 1. Assert masculine positive invariant tokens & signature features
        Assert.Contains("1man", req.Prompt);
        Assert.Contains("masculine face", req.Prompt);
        Assert.Contains("obsidian knight pauldron crest", req.Prompt);
        Assert.DoesNotContain("1girl", req.Prompt);

        // 2. Assert anti-female negative invariant tokens & feature exclusions
        Assert.NotNull(req.NegativePrompt);
        Assert.Contains("1girl", req.NegativePrompt);
        Assert.Contains("female", req.NegativePrompt);
        Assert.Contains("woman", req.NegativePrompt);
        Assert.Contains("breasts", req.NegativePrompt);
        Assert.Contains("feminine face", req.NegativePrompt);
        Assert.Contains("deformed crest", req.NegativePrompt);
        Assert.DoesNotContain("1man", req.NegativePrompt);
        Assert.DoesNotContain("dress", req.NegativePrompt);
        Assert.DoesNotContain("skirt", req.NegativePrompt);

        // 3. Assert Slot 1 is the canonical avatar (IMMUTABLE) and Slot 2 is Turn 1 image
        Assert.Equal(canonicalAvatar, req.ReferenceImageUrl);
        Assert.Equal("https://cdn.project00.ai/scenes/valerius_t1.png", req.PreviousSceneImageUrl);

        // 4. Assert dynamic Slot 2 parameters are preserved in request
        Assert.NotNull(req.ParametersJson);
        using var doc = JsonDocument.Parse(req.ParametersJson);
        var sc = doc.RootElement.GetProperty("sceneContinuity");
        Assert.Equal(0.15, sc.GetProperty("weight").GetDouble(), precision: 2);
        Assert.Equal(0.30, sc.GetProperty("endAt").GetDouble(), precision: 2);
    }

    private sealed class CapturingImageGenerationService : IImageGenerationService
    {
        public ImageGenerationRequest? CapturedRequest { get; private set; }

        public Task<string> GenerateImageAsync(string prompt, int width, int height, CancellationToken ct = default)
        {
            return Task.FromResult("https://cdn.project00.ai/scenes/legacy_gen.png");
        }

        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            CapturedRequest = request;
            return Task.FromResult("https://cdn.project00.ai/scenes/valerius_t2.png");
        }

        public Task<ImageGenerationResult> GenerateImageWithResultAsync(ImageGenerationRequest request, CancellationToken ct = default)
        {
            CapturedRequest = request;
            return Task.FromResult(new ImageGenerationResult(
                ImageUrl: "https://cdn.project00.ai/scenes/valerius_t2.png",
                Provider: "ComfyUI",
                ProviderJobId: "job-1",
                DurationMs: 1200,
                Seed: 123456,
                MetadataJson: "{\"provider\":\"ComfyUI\"}"
            ));
        }
    }

    private sealed class DummySceneStateTracker : ISceneStateTrackerService
    {
        public SceneStateDelta NextDelta { get; set; } = new();

        public Task<SessionSceneState> TrackAndExtractStateAsync(
            Character character,
            SessionSceneState? currentState,
            string userMessage,
            string assistantReply,
            CancellationToken ct = default)
        {
            var current = currentState ?? new SessionSceneState("Armory", "Center", "Armor", "Day", null, "Quiet", 0, DateTime.UtcNow);
            return Task.FromResult(current.ApplyDelta(NextDelta));
        }

        public Task<SceneStateDelta> TrackAndExtractDeltaAsync(
            Character character,
            SessionSceneState? currentState,
            string userMessage,
            string assistantReply,
            CancellationToken ct = default)
        {
            return Task.FromResult(NextDelta);
        }
    }
}
