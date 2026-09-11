using Application.DTOs;
using Application.Exceptions;
using Application.Interfaces;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.ImageGeneration;
using Infrastructure.ImageGeneration.ComfyUI;
using Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Tests.GenerationProduction;

public sealed class WorkflowModelResolutionTests
{
    private static IModelRegistry CreateStandardRegistry() => new ConfigurationModelRegistry();

    [Theory]
    [InlineData("meinamix", "meinamix_meinaV11.safetensors")]
    [InlineData("epicrealism", "epicrealism_naturalSin.safetensors")]
    [InlineData("meinamix_meinaV11.safetensors", "meinamix_meinaV11.safetensors")]
    [InlineData("epicrealism_naturalSin.safetensors", "epicrealism_naturalSin.safetensors")]
    [InlineData("epicrealism_naturalSinRC1VAE.safetensors", "epicrealism_naturalSinRC1VAE.safetensors")]
    public void Test1_VisualIdentityV1Builder_HandlesAndResolvesCanonicalAndLegacyModels(string requestModel, string expectedArtifact)
    {
        var registry = CreateStandardRegistry();
        var builder = new VisualIdentityWorkflowV1Builder(registry);

        Assert.True(builder.CanHandle("VisualIdentity", 1, requestModel));

        var req = new ImageGenerationRequest(
            Prompt: "masterpiece, 1girl in futuristic city",
            Model: requestModel,
            Seed: 42
        );

        var graph = builder.BuildWorkflow(req, "face_crop.png");

        Assert.NotNull(graph);
        var node4 = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["4"]);
        var inputs = Assert.IsAssignableFrom<Dictionary<string, object>>(node4["inputs"]);
        Assert.Equal(expectedArtifact, inputs["ckpt_name"]);
    }

    [Theory]
    [InlineData("meinamix", "meinamix_meinaV11.safetensors")]
    [InlineData("epicrealism", "epicrealism_naturalSin.safetensors")]
    [InlineData("epicrealism_naturalSinRC1VAE.safetensors", "epicrealism_naturalSinRC1VAE.safetensors")]
    public void Test2_VisualContinuityV2Builder_HandlesAndResolvesCanonicalModels(string requestModel, string expectedArtifact)
    {
        var registry = CreateStandardRegistry();
        var builder = new VisualContinuityWorkflowV2Builder(registry);

        Assert.True(builder.CanHandle("VisualContinuity", 2, requestModel));

        var req = new ImageGenerationRequest(
            Prompt: "masterpiece, 1girl walking in garden",
            Model: requestModel,
            Seed: 42
        );

        var graph = builder.BuildWorkflow(req, "face_crop.png", "previous_scene.png");

        Assert.NotNull(graph);
        var node4 = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["4"]);
        var inputs = Assert.IsAssignableFrom<Dictionary<string, object>>(node4["inputs"]);
        Assert.Equal(expectedArtifact, inputs["ckpt_name"]);
    }

    [Theory]
    [InlineData("meinamix", "meinamix_meinaV11.safetensors")]
    [InlineData("epicrealism", "epicrealism_naturalSin.safetensors")]
    [InlineData("epicrealism_naturalSinRC1VAE.safetensors", "epicrealism_naturalSinRC1VAE.safetensors")]
    public void Test3_TextToImageV1Builder_HandlesAndResolvesCanonicalModels(string requestModel, string expectedArtifact)
    {
        var registry = CreateStandardRegistry();
        var builder = new TextToImageWorkflowV1Builder(registry);

        Assert.True(builder.CanHandle("TextToImage", 1, requestModel));

        var req = new ImageGenerationRequest(
            Prompt: "masterpiece, sunset over ocean",
            Model: requestModel,
            Seed: 42
        );

        var graph = builder.BuildWorkflow(req, "dummy.png");

        Assert.NotNull(graph);
        var node4 = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["4"]);
        var inputs = Assert.IsAssignableFrom<Dictionary<string, object>>(node4["inputs"]);
        Assert.Equal(expectedArtifact, inputs["ckpt_name"]);
    }

    [Theory]
    [InlineData("anime3xl")] // SDXL family
    [InlineData("flux-dev")] // Flux family
    public void Test4_Sd15Builders_RejectIncompatibleModelFamilies(string incompatibleModel)
    {
        var registry = CreateStandardRegistry();
        var v1Builder = new VisualIdentityWorkflowV1Builder(registry);
        var v2Builder = new VisualContinuityWorkflowV2Builder(registry);
        var t2iBuilder = new TextToImageWorkflowV1Builder(registry);

        // CanHandle must reject incompatible families
        Assert.False(v1Builder.CanHandle("VisualIdentity", 1, incompatibleModel));
        Assert.False(v2Builder.CanHandle("VisualContinuity", 2, incompatibleModel));
        Assert.False(t2iBuilder.CanHandle("TextToImage", 1, incompatibleModel));

        // BuildWorkflow must throw GpuNonTransientException
        var req = new ImageGenerationRequest("prompt", Model: incompatibleModel, Seed: 123);
        Assert.Throws<GpuNonTransientException>(() => v1Builder.BuildWorkflow(req, "face.png"));
        Assert.Throws<GpuNonTransientException>(() => v2Builder.BuildWorkflow(req, "face.png", "prev.png"));
        Assert.Throws<GpuNonTransientException>(() => t2iBuilder.BuildWorkflow(req, "dummy.png"));
    }

    [Fact]
    public void Test5_CustomConfiguredSd15Model_IsDynamicallySupportedWithoutCodeChanges()
    {
        const string customId = "dreamshaper-8";
        const string customArtifact = "dreamshaper_v8.safetensors";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProviders:ImageGeneration:Models:0:Id"] = customId,
                ["AiProviders:ImageGeneration:Models:0:Family"] = "Sd15",
                ["AiProviders:ImageGeneration:Models:0:ArtifactName"] = customArtifact
            })
            .Build();

        var registry = new ConfigurationModelRegistry(config);
        var builder = new VisualIdentityWorkflowV1Builder(registry);

        // Dynamically supported via ModelFamily.Sd15 matching!
        Assert.True(builder.CanHandle("VisualIdentity", 1, customId));
        Assert.True(builder.CanHandle("VisualIdentity", 1, customArtifact));

        var req = new ImageGenerationRequest("portrait of warrior", Model: customId, Seed: 99);
        var graph = builder.BuildWorkflow(req, "ref.png");

        var node4 = Assert.IsAssignableFrom<Dictionary<string, object>>(graph["4"]);
        var inputs = Assert.IsAssignableFrom<Dictionary<string, object>>(node4["inputs"]);
        Assert.Equal(customArtifact, inputs["ckpt_name"]);
    }

    [Fact]
    public void Test6_WorkflowCapabilityPolicy_WithModelRegistry_ValidatesCorrectly()
    {
        var registry = CreateStandardRegistry();
        var policy = WorkflowCapabilityPolicy.CreateDefault(registry);

        // Supported SD1.5 models
        Assert.True(policy.IsSupported(new ImageGenerationCapability("meinamix", "VisualIdentity", 1)));
        Assert.True(policy.IsSupported(new ImageGenerationCapability("epicrealism", "VisualIdentity", 1)));
        Assert.True(policy.SupportsIdentityConditioning(new ImageGenerationCapability("meinamix", "VisualIdentity", 1)));

        // Unsupported families on SD1.5 workflows
        Assert.False(policy.IsSupported(new ImageGenerationCapability("anime3xl", "VisualIdentity", 1)));
        Assert.False(policy.IsSupported(new ImageGenerationCapability("flux-dev", "VisualIdentity", 1)));
    }
}
