using Application.DTOs;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualContinuity;

public sealed class VisualStateEvolutionTests
{
    private readonly VisualContinuityResolver _resolver;

    public VisualStateEvolutionTests()
    {
        _resolver = new VisualContinuityResolver(NullLogger<VisualContinuityResolver>.Instance);
    }

    [Fact]
    public async Task SameScene_PreservesEnvironmentAndOutfit_WhileUpdatingActionAndPose()
    {
        // Arrange
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var prevTurnId = Guid.NewGuid();
        var currTurnId = Guid.NewGuid();

        var prevSpec = new SceneSpecification(
            characterId: charId,
            location: "Grand Royal Library",
            action: "reading ancient tome",
            sceneRevision: 1,
            sessionId: sessionId,
            turnId: prevTurnId,
            pose: "seated at mahogany desk",
            weather: "Rainy",
            timeOfDay: "Evening",
            lighting: "warm candlelight",
            mood: "scholarly quiet",
            outfitContext: "Emerald Academic Robes"
        );

        var context = new SceneCompositionContext(
            CharacterId: charId,
            SessionId: sessionId,
            TurnId: currTurnId,
            SceneRevision: 2,
            PreviousScene: prevSpec,
            CharacterVisualProfile: new CharacterVisualProfile(charId, "Emerald Academic Robes", "Violet", "Silver", "Porcelain", "Scholar")
        );

        var intent = new SceneIntent(
            characterId: charId,
            locationHint: "Grand Royal Library",
            actionHint: "walks towards the stained glass window",
            poseHint: "standing near the window",
            sessionId: sessionId,
            turnId: currTurnId
        );

        // Act
        var result = await _resolver.ResolveAsync(new VisualContinuityRequest(intent, context, 2));

        // Assert
        Assert.Equal(SceneTransitionType.SameScene, result.TransitionType);
        Assert.Equal("Grand Royal Library", result.SceneVisualState.Location);
        Assert.Equal("Emerald Academic Robes", result.SceneVisualState.CharacterState.Outfit);
        Assert.Equal("Rainy", result.SceneVisualState.Weather);
        Assert.Equal("Evening", result.SceneVisualState.TimeOfDay);
        Assert.Equal("warm candlelight", result.SceneVisualState.Lighting);

        // Action and Pose are evolved
        Assert.Equal("walks towards the stained glass window", result.SceneVisualState.CharacterState.Action);
        Assert.Equal("standing near the window", result.SceneVisualState.CharacterState.Pose);
        Assert.Contains("Action", result.ChangedFields);
        Assert.Contains("Pose", result.ChangedFields);
        Assert.Contains("Outfit", result.PreservedFields);
        Assert.Contains("Weather", result.PreservedFields);
    }

    [Fact]
    public async Task LocationTransition_ResetsLocationSpecificEnvironment_PreservesCharacterAppearance()
    {
        // Arrange
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var prevSpec = new SceneSpecification(
            characterId: charId,
            location: "Grand Royal Library",
            action: "reading",
            sceneRevision: 1,
            sessionId: sessionId,
            weather: "Rainy",
            timeOfDay: "Evening",
            lighting: "warm candlelight",
            outfitContext: "Emerald Academic Robes"
        );

        var context = new SceneCompositionContext(
            CharacterId: charId,
            SessionId: sessionId,
            SceneRevision: 2,
            PreviousScene: prevSpec,
            CharacterVisualProfile: new CharacterVisualProfile(charId, "Emerald Academic Robes", "Violet", "Silver", "Porcelain", "Scholar")
        );

        var intent = new SceneIntent(
            characterId: charId,
            locationHint: "Sunny Courtyard Garden",
            actionHint: "strolls among blooming flowers",
            sessionId: sessionId
        );

        // Act
        var result = await _resolver.ResolveAsync(new VisualContinuityRequest(intent, context, 2));

        // Assert
        Assert.Equal(SceneTransitionType.LocationTransition, result.TransitionType);
        Assert.Equal("Sunny Courtyard Garden", result.SceneVisualState.Location);
        Assert.Equal("Emerald Academic Robes", result.SceneVisualState.CharacterState.Outfit);
        Assert.Equal("Clear", result.SceneVisualState.Weather);
        Assert.Equal("Daytime", result.SceneVisualState.TimeOfDay);
        Assert.Contains("Location", result.ChangedFields);
        Assert.Contains("Outfit", result.PreservedFields);
    }

    [Fact]
    public async Task ExplicitOverride_ImmediatelyMutatesAppearance_WithoutContradiction()
    {
        // Arrange
        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var prevSpec = new SceneSpecification(
            characterId: charId,
            location: "Bedchamber",
            action: "resting",
            sceneRevision: 1,
            sessionId: sessionId,
            outfitContext: "Red Silk Nightgown"
        );

        var context = new SceneCompositionContext(
            CharacterId: charId,
            SessionId: sessionId,
            SceneRevision: 2,
            PreviousScene: prevSpec
        );

        var intent = new SceneIntent(
            characterId: charId,
            locationHint: "Bedchamber",
            actionHint: "changes attire for travel",
            outfitHint: "Leather Riding Coat with silver buckles",
            sessionId: sessionId
        );

        // Act
        var result = await _resolver.ResolveAsync(new VisualContinuityRequest(intent, context, 2));

        // Assert
        Assert.Equal("Leather Riding Coat with silver buckles", result.SceneVisualState.CharacterState.Outfit);
        Assert.Equal("CurrentIntent", result.Provenance.OutfitSource);
        Assert.Contains("Outfit", result.ChangedFields);
    }
}
