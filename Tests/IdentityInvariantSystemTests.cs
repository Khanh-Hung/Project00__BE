using System.Text.Json;
using Application.Abstractions.Data;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tests;

public sealed class IdentityInvariantSystemTests
{
    private readonly VisualPromptCompiler _compiler = new();

    [Fact]
    public void CompileNegativePrompt_WhenGenderIsMale_SuppressesFemaleTokensWithoutRestrictingClothing()
    {
        var identity = new CharacterVisualIdentity(
            GenderPresentation: GenderPresentation.Male,
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
            GenderPresentation: GenderPresentation.Female,
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
            GenderPresentation: GenderPresentation.NonBinary,
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
            GenderPresentation: GenderPresentation.Male,
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
            GenderPresentation: GenderPresentation.Female,
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

        var (weight, endAt, isActive) = policy.Resolve(isColdStart, isTransition);

        Assert.Equal(expectedWeight, weight, precision: 4);
        Assert.Equal(expectedEndAt, endAt, precision: 4);
        Assert.Equal(expectedActive, isActive);
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
            GenderPresentation: GenderPresentation.Male,
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
