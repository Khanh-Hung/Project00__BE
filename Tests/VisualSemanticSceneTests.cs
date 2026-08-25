using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using System.Collections.Immutable;
using Xunit;

namespace Tests;

public sealed class VisualSemanticSceneTests
{
    private readonly VisualPromptCompiler _compiler = new();

    [Fact]
    public void VisualSnapshot_With_SceneDescription_Strictly_Preserves_All_Persistent_And_Transient_State()
    {
        // Arrange
        var turnId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var identity = new CharacterVisualIdentity(
            Gender: "Female",
            Face: "delicate face, sharp jawline",
            Hair: "long silver hair in neat updo",
            Eyes: "sharp emerald green eyes",
            Skin: "porcelain white skin",
            Body: "slender build",
            ClothingStyle: "black gothic lolita dress",
            Accessories: "silver hairpin, obsidian pendant",
            CanonicalReferenceUrl: "https://cloud.storage/seraphina_avatar.png",
            FullBodyUrl: "https://cloud.storage/seraphina_fullbody.png"
        );

        var sceneState = new SessionSceneState(
            CurrentLocation: "Grand Library",
            CurrentPosition: "At Reading Desk",
            CurrentOutfit: "black gothic lolita dress",
            CurrentTimeOfDay: "Midnight",
            HeldItems: "ancient grimoire",
            Atmosphere: "Quiet solemn tension",
            SceneRevision: 3,
            LastUpdatedAt: DateTime.UtcNow
        );

        var transient = new TransientVisualState(
            Pose: "Sitting gracefully",
            Action: "Turning dusty page",
            Expression: "melancholy eyes, gentle sadness, soft frown",
            Gaze: "looking down at grimoire"
        );

        var sceneDesc = new VisualSceneDescription(
            shotType: "medium shot",
            cameraAngle: "slight 3/4 turn",
            subjectPlacement: "centered",
            detailedAction: "sitting at wooden reading desk, carefully turning dusty grimoire page",
            detailedEnvironment: "grand library with towering bookshelves, candle on reading desk",
            lightingStyle: "warm candlelight casting soft shadows, dark room ambiance",
            atmosphere: "quiet solemn tension, mystical mood",
            englishPromptTags: new[]
            {
                "medium shot",
                "slight 3/4 turn",
                "sitting at wooden desk",
                "grand library background",
                "warm candlelight",
                "mystical atmosphere"
            }
        );

        var profile = GenerationProfile.CreateDefault(
            seed: 424242,
            workflow: "VisualIdentity",
            workflowVersion: 1
        );

        var snapshot = VisualSnapshot.Create(
            turnId: turnId,
            sessionId: sessionId,
            characterId: charId,
            sceneRevision: 3,
            visualIdentity: identity,
            sceneState: sceneState,
            transientState: transient,
            generationProfile: profile,
            sceneDescription: sceneDesc
        );

        // Act - Compile prompt
        var prompt = _compiler.CompileScenePrompt(snapshot);

        // Assert - Tier 1: Character Physical Identity
        Assert.Contains("masterpiece, best quality, solo, 1girl", prompt);
        Assert.Contains("long silver hair in neat updo", prompt);
        Assert.Contains("sharp emerald green eyes", prompt);
        Assert.Contains("porcelain white skin", prompt);
        Assert.Contains("silver hairpin", prompt);

        // Assert - Tier 2: Persistent Scene State (NEVER omitted or overridden by SceneDescription!)
        Assert.Contains("wearing black gothic lolita dress", prompt);
        Assert.Contains("at At Reading Desk, in Grand Library", prompt);
        Assert.Contains("Midnight", prompt);
        Assert.Contains("holding ancient grimoire", prompt);

        // Assert - Tier 3: Transient Action & Emotional Expression (NEVER omitted!)
        Assert.Contains("melancholy eyes", prompt);
        Assert.Contains("gentle sadness", prompt);
        Assert.Contains("looking down at grimoire", prompt);
        Assert.Contains("Sitting gracefully", prompt);
        Assert.Contains("Turning dusty page", prompt);

        // Assert - Tier 4: Structured Cinematic Description (Complements and enriches the scene)
        Assert.Contains("medium shot", prompt);
        Assert.Contains("slight 3/4 turn", prompt);
        Assert.Contains("grand library background", prompt);
        Assert.Contains("warm candlelight", prompt);

        // Assert - Tier 5: Quality & Style Anchors
        Assert.Contains("soft painterly anime aesthetic", prompt);
        Assert.Contains("8k, pixiv trending", prompt);
    }

    [Fact]
    public void VisualSceneDescription_Is_Deep_Immutable_And_Copies_Defensively()
    {
        var mutableList = new List<string> { "tag1", "tag2", "tag3" };

        var desc = new VisualSceneDescription(
            shotType: "close-up portrait",
            cameraAngle: "eye level",
            englishPromptTags: mutableList
        );

        // Mutating the original external list
        mutableList.Add("malicious_tag_injected_later");

        // Assert - Internal state remains purely immutable and unchanged
        Assert.Equal(3, desc.EnglishPromptTags.Length);
        Assert.DoesNotContain("malicious_tag_injected_later", desc.EnglishPromptTags);
        Assert.IsType<ImmutableArray<string>>(desc.EnglishPromptTags);
    }

    [Fact]
    public void PromptCompiler_Deduplicates_Tags_CaseInsensitively_Preserving_Determinism()
    {
        var turnId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        var identity = new CharacterVisualIdentity(
            Gender: "Female",
            Hair: "silver hair",
            Eyes: "green eyes"
        );

        var sceneState = new SessionSceneState(
            CurrentLocation: "Sanctuary",
            CurrentPosition: "Altar",
            CurrentOutfit: "white robe",
            CurrentTimeOfDay: "Dawn",
            HeldItems: null,
            Atmosphere: "Peaceful",
            SceneRevision: 1,
            LastUpdatedAt: DateTime.UtcNow
        );

        var transient = new TransientVisualState(
            Pose: "standing",
            Action: "praying",
            Expression: "serene smile"
        );

        var sceneDesc = new VisualSceneDescription(
            shotType: "medium shot",
            cameraAngle: "eye level",
            englishPromptTags: new[] { "standing", "Silver Hair", "WHITE ROBE", "peaceful" } // overlaps with existing tags
        );

        var profile = GenerationProfile.CreateDefault(seed: 12345);

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

        var prompt1 = _compiler.CompileScenePrompt(snapshot);
        var prompt2 = _compiler.CompileScenePrompt(snapshot);

        // Determinism assertion
        Assert.Equal(prompt1, prompt2);

        // Assert - Overlapping tag "standing" only occurs once
        var occurrences = prompt1.Split(", ").Count(t => t.Equals("standing", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, occurrences);
    }
}
