using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Xunit;

namespace Tests;

public sealed class VisualSemanticSceneTests
{
    private readonly VisualPromptCompiler _compiler = new();

    [Fact]
    public void VisualSnapshot_With_Structured_SceneDescription_Compiles_Deterministic_English_Prompt()
    {
        var turnId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var identity = new CharacterVisualIdentity(
            Gender: "Female",
            Face: "delicate face, sharp jawline",
            Hair: "long silver hair in neat updo with hairpin",
            Eyes: "sharp emerald green eyes",
            Skin: "porcelain white skin",
            Body: "slender build",
            ClothingStyle: "white gown with dark green corset",
            Accessories: "golden gear hair accessory, jade earrings",
            CanonicalReferenceUrl: "https://cloud.storage/seraphina_face_crop.png",
            FullBodyUrl: "https://cloud.storage/seraphina_fullbody.png"
        );

        var sceneState = new SessionSceneState(
            CurrentLocation: "Steampunk Workshop",
            CurrentPosition: "Beside Workbench",
            CurrentOutfit: "white gown with dark green corset",
            CurrentTimeOfDay: "Daytime",
            HeldItems: "brass wrench",
            Atmosphere: "Quiet tension",
            SceneRevision: 1,
            LastUpdatedAt: DateTime.UtcNow
        );

        var transient = new TransientVisualState(
            Pose: "Standing",
            Action: "Stepping forward to cover blueprint",
            Expression: "Sharp cautious gaze"
        );

        var sceneDesc = new VisualSceneDescription(
            ShotType: "medium shot",
            CameraAngle: "slight 3/4 turn",
            SubjectPlacement: "centered",
            DetailedAction: "standing beside wooden workbench, hand covering technical blueprint, holding brass wrench",
            DetailedEnvironment: "steampunk workshop, copper pipes, glowing crystal lantern, workbench in foreground",
            LightingStyle: "warm indoor lantern light, soft rim glow, glowing floating particles",
            Atmosphere: "cautious tension, inquisitive, quiet ambiance",
            EnglishPromptTags: new List<string>
            {
                "medium shot",
                "slight 3/4 turn",
                "looking at viewer",
                "standing beside wooden workbench",
                "hand shielding technical blueprint",
                "holding brass wrench",
                "steampunk workshop background",
                "copper pipes and gears",
                "warm lantern lighting",
                "glowing floating particles",
                "cautious atmosphere"
            }
        );

        var profile = GenerationProfile.CreateDefault(
            seed: 12345,
            workflow: "VisualIdentity",
            workflowVersion: 1,
            parametersJson: "{\"ipAdapter\":{\"weight\":0.55,\"endAt\":0.75}}"
        );

        var snapshot = VisualSnapshot.Create(
            turnId: turnId,
            sessionId: sessionId,
            characterId: charId,
            sceneRevision: 1,
            visualIdentity: identity,
            sceneState: sceneState,
            transientState: transient,
            generationProfile: profile,
            sceneDescription: sceneDesc
        );

        // Act - Compile multiple times to verify 100% determinism
        var prompt1 = _compiler.CompileScenePrompt(snapshot);
        var prompt2 = _compiler.CompileScenePrompt(snapshot);

        // Assert - Determinism invariant
        Assert.Equal(prompt1, prompt2);
        Assert.NotEmpty(prompt1);

        // Assert - Identity invariants are maintained in prompt
        Assert.Contains("masterpiece, best quality, solo, 1girl", prompt1);
        Assert.Contains("long silver hair", prompt1);
        Assert.Contains("sharp emerald green eyes", prompt1);

        // Assert - Scene composition invariants are strictly reflected
        Assert.Contains("medium shot", prompt1);
        Assert.Contains("slight 3/4 turn", prompt1);
        Assert.Contains("standing beside wooden workbench", prompt1);
        Assert.Contains("hand shielding technical blueprint", prompt1);
        Assert.Contains("holding brass wrench", prompt1);
        Assert.Contains("steampunk workshop background", prompt1);

        // Assert - Aesthetic quality anchors
        Assert.Contains("soft painterly anime aesthetic", prompt1);
        Assert.Contains("8k, pixiv trending", prompt1);
    }

    [Fact]
    public void VisualSnapshot_Without_SceneDescription_Gracefully_Falls_Back_To_Transient_And_SceneState()
    {
        var turnId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var identity = new CharacterVisualIdentity(
            Gender: "Female",
            Hair: "golden blonde twin tails",
            Eyes: "sapphire blue eyes",
            ClothingStyle: "black gothic lolita dress"
        );

        var sceneState = new SessionSceneState(
            CurrentLocation: "Library",
            CurrentPosition: "At Reading Desk",
            CurrentOutfit: "black gothic lolita dress",
            CurrentTimeOfDay: "Night",
            HeldItems: "ancient grimoire",
            Atmosphere: "Mysterious",
            SceneRevision: 2,
            LastUpdatedAt: DateTime.UtcNow
        );

        var transient = new TransientVisualState(
            Pose: "Sitting gracefully",
            Action: "Reading page",
            Expression: "Curious smile"
        );

        var profile = GenerationProfile.CreateDefault(
            seed: 67890,
            workflow: "VisualIdentity",
            workflowVersion: 1
        );

        var snapshot = VisualSnapshot.Create(
            turnId: turnId,
            sessionId: sessionId,
            characterId: charId,
            sceneRevision: 2,
            visualIdentity: identity,
            sceneState: sceneState,
            transientState: transient,
            generationProfile: profile,
            sceneDescription: null // No structured description
        );

        var compiled = _compiler.CompileScenePrompt(snapshot);

        Assert.Contains("masterpiece, best quality, solo, 1girl", compiled);
        Assert.Contains("golden blonde twin tails", compiled);
        Assert.Contains("sapphire blue eyes", compiled);
        Assert.Contains("wearing black gothic lolita dress", compiled);
        Assert.Contains("Sitting gracefully", compiled);
        Assert.Contains("Reading page", compiled);
        Assert.Contains("at At Reading Desk, in Library", compiled);
        Assert.Contains("holding ancient grimoire", compiled);
    }
}
