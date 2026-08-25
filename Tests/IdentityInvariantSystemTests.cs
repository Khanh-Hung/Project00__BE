using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Xunit;

namespace Tests;

public sealed class IdentityInvariantSystemTests
{
    private readonly VisualPromptCompiler _compiler = new();

    [Fact]
    public void CompileNegativePrompt_WhenGenderIsMale_SuppressesFemaleTokens()
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
    public void CanonicalAvatarReference_RemainsImmutableAcrossTurns()
    {
        var canonicalAvatarUrl = "https://cdn.project00.ai/avatars/valerius_canonical.png";
        var identity = new CharacterVisualIdentity(
            CanonicalReferenceUrl: canonicalAvatarUrl,
            GenderPresentation: GenderPresentation.Male
        );

        var snapshotT1 = VisualSnapshot.Create(
            turnId: Guid.NewGuid(),
            sessionId: Guid.NewGuid(),
            characterId: Guid.NewGuid(),
            sceneRevision: 1,
            visualIdentity: identity,
            sceneState: new SessionSceneState("Armory", "Bench", "Armor", "Day", null, "Quiet", 1, DateTime.UtcNow),
            transientState: null,
            generationProfile: GenerationProfile.CreateDefault(),
            previousSceneImageUrl: null
        );

        var snapshotT2 = VisualSnapshot.Create(
            turnId: Guid.NewGuid(),
            sessionId: snapshotT1.SessionId,
            characterId: snapshotT1.CharacterId,
            sceneRevision: 2,
            visualIdentity: identity,
            sceneState: new SessionSceneState("Armory", "Anvil", "Armor", "Day", null, "Quiet", 2, DateTime.UtcNow),
            transientState: null,
            generationProfile: GenerationProfile.CreateDefault(),
            previousSceneImageUrl: "https://cdn.project00.ai/scenes/valerius_turn_1.png"
        );

        Assert.Equal(canonicalAvatarUrl, snapshotT1.IdentityReferenceUrl);
        Assert.Equal(canonicalAvatarUrl, snapshotT2.IdentityReferenceUrl);
        Assert.NotEqual(snapshotT2.PreviousSceneImageUrl, snapshotT2.IdentityReferenceUrl);
    }
}
