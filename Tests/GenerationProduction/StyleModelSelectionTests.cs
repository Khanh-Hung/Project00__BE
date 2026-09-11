using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.ImageGeneration;
using Infrastructure.ImageGeneration.ComfyUI;
using Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Tests.GenerationProduction;

public sealed class StyleModelSelectionTests
{
    private static Character CreateCharacterWithStyle(VisualStyle style)
    {
        var identity = new CharacterVisualIdentity(
            Style: style.ToString(),
            VisualStyle: style,
            CanonicalReferenceUrl: "https://cdn.project00.ai/aria_face.png"
        );

        return new Character(
            name: $"Aria_{style}",
            title: "Protagonist",
            avatarUrl: "https://cdn.project00.ai/avatar.png",
            personalityPrompt: "Determined warrior",
            greeting: "Greetings",
            category: "General",
            visualIdentity: identity
        );
    }

    [Fact]
    public void Test1_AnimeCharacter_ResolvesToMeinamix()
    {
        var registry = new ConfigurationModelRegistry();
        var provider = new VisualGenerationProfileProvider(modelRegistry: registry);
        var character = CreateCharacterWithStyle(VisualStyle.Anime);

        var profile = provider.ResolveProfile(character);

        Assert.Equal("meinamix", profile.Model);
    }

    [Fact]
    public void Test2_RealisticCharacter_ResolvesToEpicrealism()
    {
        var registry = new ConfigurationModelRegistry();
        var provider = new VisualGenerationProfileProvider(modelRegistry: registry);
        var character = CreateCharacterWithStyle(VisualStyle.Realistic);

        var profile = provider.ResolveProfile(character);

        Assert.Equal("epicrealism", profile.Model);
    }

    [Fact]
    public void Test3_CinematicCharacter_ResolvesToEpicrealism()
    {
        var registry = new ConfigurationModelRegistry();
        var provider = new VisualGenerationProfileProvider(modelRegistry: registry);
        var character = CreateCharacterWithStyle(VisualStyle.Cinematic);

        var profile = provider.ResolveProfile(character);

        Assert.Equal("epicrealism", profile.Model);
    }

    [Fact]
    public void Test4_ManhwaCharacter_ResolvesToMeinamix()
    {
        var registry = new ConfigurationModelRegistry();
        var provider = new VisualGenerationProfileProvider(modelRegistry: registry);
        var character = CreateCharacterWithStyle(VisualStyle.Manhwa);

        var profile = provider.ResolveProfile(character);

        Assert.Equal("meinamix", profile.Model);
    }

    [Fact]
    public void Test5_CharacterWithoutVisualIdentity_FallsBackToDefaultModel()
    {
        var registry = new ConfigurationModelRegistry();
        var provider = new VisualGenerationProfileProvider(modelRegistry: registry);
        var character = new Character("Hero", "Knight", "avatar.png", "Prompt", "Hello", "Anime");

        var profile = provider.ResolveProfile(character);

        Assert.Equal(VisualGenerationProfileProvider.DefaultModelId, profile.Model);
        Assert.Equal("meinamix", profile.Model);
    }

    [Fact]
    public void Test6_Configuration_OverridesStyleModelMapping()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProviders:ImageGeneration:StyleModels:Realistic"] = "custom-realistic-v2",
                ["AiProviders:ImageGeneration:StyleModels:Anime"] = "custom-anime-v3"
            })
            .Build();

        var provider = new VisualGenerationProfileProvider(configuration: config);

        var realisticChar = CreateCharacterWithStyle(VisualStyle.Realistic);
        var animeChar = CreateCharacterWithStyle(VisualStyle.Anime);

        Assert.Equal("custom-realistic-v2", provider.ResolveProfile(realisticChar).Model);
        Assert.Equal("custom-anime-v3", provider.ResolveProfile(animeChar).Model);
    }

    [Fact]
    public void Test7_EndToEnd_AnimeAndRealisticCharacters_PropagateThroughToComfyUIWorkflowCheckpoints()
    {
        var registry = new ConfigurationModelRegistry();
        var provider = new VisualGenerationProfileProvider(modelRegistry: registry);
        var policy = WorkflowCapabilityPolicy.CreateDefault(registry);
        var builder = new VisualIdentityWorkflowV1Builder(registry);

        // 1. Anime character path
        var animeChar = CreateCharacterWithStyle(VisualStyle.Anime);
        var animeProfile = provider.ResolveProfile(animeChar);
        Assert.Equal("meinamix", animeProfile.Model);

        var animeSnapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: animeChar.Id,
            SceneRevision: 1,
            VisualIdentity: animeChar.VisualIdentity,
            SceneState: new SessionSceneState("City", "Center"),
            TransientState: null,
            GenerationProfile: animeProfile
        );

        var animeRequest = ImageGenerationRequest.FromSnapshot(animeSnapshot, "1girl in futuristic neon city");
        Assert.Equal("meinamix", animeRequest.Model);

        // Verify capability policy validation passes
        Assert.True(policy.IsSupported(animeRequest.ResolveEffectiveCapability()));

        // Verify ComfyUI node graph populates the anime checkpoint artifact
        var animeGraph = builder.BuildWorkflow(animeRequest, "face.png");
        var animeNode4 = (Dictionary<string, object>)animeGraph["4"];
        var animeInputs = (Dictionary<string, object>)animeNode4["inputs"];
        Assert.Equal("meinamix_meinaV11.safetensors", animeInputs["ckpt_name"]);

        // 2. Realistic character path
        var realisticChar = CreateCharacterWithStyle(VisualStyle.Realistic);
        var realisticProfile = provider.ResolveProfile(realisticChar);
        Assert.Equal("epicrealism", realisticProfile.Model);

        var realisticSnapshot = new VisualSnapshot(
            TurnId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            CharacterId: realisticChar.Id,
            SceneRevision: 1,
            VisualIdentity: realisticChar.VisualIdentity,
            SceneState: new SessionSceneState("Park", "Bench"),
            TransientState: null,
            GenerationProfile: realisticProfile
        );

        var realisticRequest = ImageGenerationRequest.FromSnapshot(realisticSnapshot, "1man sitting on park bench photorealistic");
        Assert.Equal("epicrealism", realisticRequest.Model);

        // Verify capability policy validation passes
        Assert.True(policy.IsSupported(realisticRequest.ResolveEffectiveCapability()));

        // Verify ComfyUI node graph populates the realistic checkpoint artifact
        var realisticGraph = builder.BuildWorkflow(realisticRequest, "face.png");
        var realisticNode4 = (Dictionary<string, object>)realisticGraph["4"];
        var realisticInputs = (Dictionary<string, object>)realisticNode4["inputs"];
        Assert.Equal("epicrealism_naturalSin.safetensors", realisticInputs["ckpt_name"]);
    }

    [Fact]
    public void Test8_NormalizeModelId_WithRegistry_WhenUnknownModelConfigured_ThrowsInvalidOperationException()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProviders:ImageGeneration:StyleModels:Realistic"] = "unknown-nonexistent-model"
            })
            .Build();

        var registry = new ConfigurationModelRegistry();
        var provider = new VisualGenerationProfileProvider(configuration: config, modelRegistry: registry);
        var realisticChar = CreateCharacterWithStyle(VisualStyle.Realistic);

        var ex = Assert.Throws<InvalidOperationException>(() => provider.ResolveProfile(realisticChar));
        Assert.Contains("unknown-nonexistent-model", ex.Message);
        Assert.Contains("not registered in ModelRegistry", ex.Message);
    }
}
