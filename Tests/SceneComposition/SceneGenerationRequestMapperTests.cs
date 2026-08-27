using Application.DTOs;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Xunit;

namespace Tests.SceneComposition;

public sealed class SceneGenerationRequestMapperTests
{
    [Fact]
    public void MapToVisualSnapshot_ProducesValidSnapshot_CompatibleWithPR22to30Engine()
    {
        var mapper = new SceneGenerationRequestMapper();
        var promptComposer = new ScenePromptComposer();

        var charId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var canonicalRef = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/valerius_canonical.png",
            type: VisualReferenceType.Canonical,
            status: VisualReferenceStatus.Active,
            isCanonical: true
        );

        var profile = new CharacterVisualProfile(
            characterId: charId,
            eyeColor: "Crimson Red",
            hairColor: "Silver",
            currentOutfit: "Ebony Armor"
        );

        var env = SceneEnvironment.Create(
            location: "Courtyard",
            architecture: "Stone courtyard with banners",
            lighting: "morning sunlight"
        );

        var spec = new SceneSpecification(
            characterId: charId,
            location: "Courtyard",
            action: "Drawing a sword",
            sceneRevision: 3,
            sessionId: sessionId,
            turnId: turnId,
            pose: "battle stance",
            environment: env,
            lighting: "morning sunlight"
        );

        var visualContext = new VisualContextResolutionResult(
            CharacterId: charId,
            VisualProfileVersion: 2,
            CanonicalIdentityReference: canonicalRef,
            CurrentAppearance: profile,
            PredecessorVisualMemory: null,
            RelevantOlderMemories: Array.Empty<CharacterVisualMemory>(),
            TransitionType: SceneTransitionType.SameLocation,
            SelectionSummary: "Test Context"
        );

        var genProfile = new GenerationProfile(
            Seed: 42L,
            Workflow: "VisualIdentity",
            WorkflowVersion: 1,
            Width: 1024,
            Height: 1024
        );

        var snapshot = mapper.MapToVisualSnapshot(spec, visualContext, genProfile, promptComposer);

        Assert.NotNull(snapshot);
        Assert.Equal(turnId, snapshot.TurnId);
        Assert.Equal(sessionId, snapshot.SessionId);
        Assert.Equal(charId, snapshot.CharacterId);
        Assert.Equal(3, snapshot.SceneRevision);
        Assert.Equal("https://cdn.project00.ai/valerius_canonical.png", snapshot.IdentityReferenceUrl);
        Assert.Equal("Courtyard", snapshot.SceneState.CurrentLocation);
        Assert.NotNull(snapshot.SceneDescription);
        Assert.Equal(Slot2Context.ColdStart, snapshot.Context);
    }
}
