using Application.DTOs;
using Application.Interfaces;
using Domain.Enums;
using Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Tests.GenerationProduction;

public sealed class ModelRegistryTests
{
    [Fact]
    public void Test1_BaselineRegistry_ContainsStandardModels()
    {
        IModelRegistry registry = new ConfigurationModelRegistry();

        var models = registry.GetAll();
        Assert.NotEmpty(models);
        Assert.Contains(models, m => m.Id.Equals("meinamix", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(models, m => m.Id.Equals("epicrealism", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(models, m => m.Id.Equals("anime3xl", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(models, m => m.Id.Equals("flux-dev", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("meinamix", ModelFamily.Sd15, "meinamix_meinaV11.safetensors")]
    [InlineData("MEINAMIX", ModelFamily.Sd15, "meinamix_meinaV11.safetensors")]
    [InlineData("epicrealism", ModelFamily.Sd15, "epicrealism_naturalSinRC1VAE.safetensors")]
    [InlineData("anime3xl", ModelFamily.Sdxl, "animagineXLV3_base.safetensors")]
    [InlineData("flux-dev", ModelFamily.Flux, "flux1-dev.safetensors")]
    public void Test2_FindById_ResolvesCanonicalModelId(string modelId, ModelFamily expectedFamily, string expectedArtifact)
    {
        IModelRegistry registry = new ConfigurationModelRegistry();

        var model = registry.FindById(modelId);

        Assert.NotNull(model);
        Assert.Equal(expectedFamily, model.Family);
        Assert.Equal(expectedArtifact, model.ArtifactName);
    }

    [Theory]
    [InlineData("meinamix_meinaV11.safetensors", "meinamix", ModelFamily.Sd15, "meinamix_meinaV11.safetensors")]
    [InlineData("MEINAMIX_MEINAV11.SAFETENSORS", "meinamix", ModelFamily.Sd15, "meinamix_meinaV11.safetensors")]
    [InlineData("epicrealism_naturalSinRC1VAE.safetensors", "epicrealism", ModelFamily.Sd15, "epicrealism_naturalSinRC1VAE.safetensors")]
    [InlineData("epicrealism_naturalSin.safetensors", "epicrealism", ModelFamily.Sd15, "epicrealism_naturalSinRC1VAE.safetensors")]
    [InlineData("animagineXLV3_base.safetensors", "anime3xl", ModelFamily.Sdxl, "animagineXLV3_base.safetensors")]
    [InlineData("flux1-dev.safetensors", "flux-dev", ModelFamily.Flux, "flux1-dev.safetensors")]
    public void Test3_FindById_WithLegacyArtifactName_ResolvesViaFallbackSecondaryIndex(
        string artifactName, string expectedId, ModelFamily expectedFamily, string expectedCanonicalArtifact)
    {
        IModelRegistry registry = new ConfigurationModelRegistry();

        // Calling FindById with the physical artifact filename (legacy or direct filename behavior)
        var model = registry.FindById(artifactName);

        Assert.NotNull(model);
        // The resolved model maintains its canonical ModelId, proving artifact name is decoupled from ModelId
        Assert.Equal(expectedId, model.Id);
        Assert.Equal(expectedFamily, model.Family);
        Assert.Equal(expectedCanonicalArtifact, model.ArtifactName, ignoreCase: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("non-existent-model")]
    [InlineData("random_file.safetensors")]
    public void Test4_FindById_WithUnknownOrEmptyIdentifier_ReturnsNull(string? modelId)
    {
        IModelRegistry registry = new ConfigurationModelRegistry();

        var model = registry.FindById(modelId!);

        Assert.Null(model);
    }

    [Fact]
    public void Test5_GetRequired_WithValidModel_ReturnsDefinition()
    {
        IModelRegistry registry = new ConfigurationModelRegistry();

        var model = registry.GetRequired("meinamix");

        Assert.NotNull(model);
        Assert.Equal("meinamix", model.Id);
        Assert.Equal(ModelFamily.Sd15, model.Family);
    }

    [Fact]
    public void Test6_GetRequired_WithUnknownModel_ThrowsKeyNotFoundException()
    {
        IModelRegistry registry = new ConfigurationModelRegistry();

        var ex = Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("unknown-model"));
        Assert.Contains("unknown-model", ex.Message);
    }

    [Fact]
    public void Test7_ConfigurationModelRegistry_OverlaysCustomConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProviders:ImageGeneration:Models:0:Id"] = "custom-sdxl",
                ["AiProviders:ImageGeneration:Models:0:Family"] = "Sdxl",
                ["AiProviders:ImageGeneration:Models:0:ArtifactName"] = "custom_sdxl_v1.safetensors",

                ["AiProviders:ImageGeneration:Models:1:Id"] = "flux-schnell",
                ["AiProviders:ImageGeneration:Models:1:Family"] = "Flux",
                ["AiProviders:ImageGeneration:Models:1:ArtifactName"] = "flux1-schnell.safetensors"
            })
            .Build();

        IModelRegistry registry = new ConfigurationModelRegistry(config);

        // Custom models exist
        var sdxlModel = registry.FindById("custom-sdxl");
        Assert.NotNull(sdxlModel);
        Assert.Equal(ModelFamily.Sdxl, sdxlModel.Family);
        Assert.Equal("custom_sdxl_v1.safetensors", sdxlModel.ArtifactName);

        var fluxModel = registry.FindById("flux-schnell");
        Assert.NotNull(fluxModel);
        Assert.Equal(ModelFamily.Flux, fluxModel.Family);
        Assert.Equal("flux1-schnell.safetensors", fluxModel.ArtifactName);

        // Legacy fallback also works for custom models
        var legacyLookup = registry.FindById("custom_sdxl_v1.safetensors");
        Assert.NotNull(legacyLookup);
        Assert.Equal("custom-sdxl", legacyLookup.Id);

        // Baseline models still exist
        var meinamix = registry.FindById("meinamix");
        Assert.NotNull(meinamix);
    }

    [Fact]
    public void Test8_ModelDefinition_RecordEquality_WorksAsExpected()
    {
        var m1 = new ModelDefinition("meinamix", ModelFamily.Sd15, "meinamix_meinaV11.safetensors");
        var m2 = new ModelDefinition("meinamix", ModelFamily.Sd15, "meinamix_meinaV11.safetensors");
        var m3 = new ModelDefinition("epicrealism", ModelFamily.Sd15, "epicrealism_naturalSinRC1VAE.safetensors");

        Assert.Equal(m1, m2);
        Assert.NotEqual(m1, m3);
    }

    [Fact]
    public void Test9_ConfigurationModelRegistry_OverridingEpicrealismArtifact_ResolvesNewCheckpoint()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProviders:ImageGeneration:Models:0:Id"] = "epicrealism",
                ["AiProviders:ImageGeneration:Models:0:Family"] = "Sd15",
                ["AiProviders:ImageGeneration:Models:0:ArtifactName"] = "epicrealism_pureInstinct.safetensors"
            })
            .Build();

        IModelRegistry registry = new ConfigurationModelRegistry(config);

        var model = registry.FindById("epicrealism");
        Assert.NotNull(model);
        Assert.Equal("epicrealism", model.Id);
        Assert.Equal(ModelFamily.Sd15, model.Family);
        Assert.Equal("epicrealism_pureInstinct.safetensors", model.ArtifactName);

        // Resolves via direct artifact name lookup as well
        var legacyLookup = registry.FindById("epicrealism_pureInstinct.safetensors");
        Assert.NotNull(legacyLookup);
        Assert.Equal("epicrealism", legacyLookup.Id);
        Assert.Equal("epicrealism_pureInstinct.safetensors", legacyLookup.ArtifactName);
    }
}
