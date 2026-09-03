using Application.Interfaces;
using Domain.Entities;
using Infrastructure;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Tests.IdentityQualityGuard;

public sealed class IdentityQualityGuardDependencyInjectionTests
{
    [Fact]
    public void DependencyInjection_WhenQualityGuardEnabledInProduction_WithStubEvaluator_ThrowsInvalidOperationException()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["AiProviders:ImageGeneration:QualityGuard:Enabled"] = "true",
            ["AiProviders:ImageGeneration:QualityGuard:EvaluatorType"] = "DevelopmentStub",
            ["AiProviders:ImageGeneration:QualityGuard:AllowStubEvaluatorInProduction"] = "false",
            ["ConnectionStrings:CoreConnection"] = "DataSource=:memory:"
        };

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        var ex = Assert.Throws<InvalidOperationException>(() => { services.AddInfrastructure(config); });
        Assert.Contains("CRITICAL STARTUP CONFIGURATION ERROR", ex.Message);
    }

    [Fact]
    public void DependencyInjection_WhenQualityGuardEnabledInProduction_WithExplicitAllowStubOptIn_Succeeds()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["AiProviders:ImageGeneration:QualityGuard:Enabled"] = "true",
            ["AiProviders:ImageGeneration:QualityGuard:EvaluatorType"] = "DevelopmentStub",
            ["AiProviders:ImageGeneration:QualityGuard:AllowStubEvaluatorInProduction"] = "true",
            ["ConnectionStrings:CoreConnection"] = "DataSource=:memory:",
            ["AiProviders:ImageProvider"] = "ComfyUI"
        };

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        services.AddInfrastructure(config);

        var sp = services.BuildServiceProvider();
        var evaluator = sp.GetRequiredService<IIdentityQualityEvaluator>();
        Assert.NotNull(evaluator);
    }

    [Fact]
    public void DependencyInjection_WhenCustomEvaluatorRegistered_AllowsProductionStartupWithoutStubError()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["AiProviders:ImageGeneration:QualityGuard:Enabled"] = "true",
            ["AiProviders:ImageGeneration:QualityGuard:EvaluatorType"] = "Clip",
            ["ConnectionStrings:CoreConnection"] = "DataSource=:memory:",
            ["AiProviders:ImageProvider"] = "ComfyUI"
        };

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Custom ML Evaluator registered by Composition Root
        services.AddScoped<IIdentityQualityEvaluator, TestClipEvaluator>();

        // Act: AddInfrastructure should NOT throw Unknown EvaluatorType and must preserve the registered evaluator
        services.AddInfrastructure(config);

        var sp = services.BuildServiceProvider();
        var evaluator = sp.GetRequiredService<IIdentityQualityEvaluator>();
        Assert.IsType<TestClipEvaluator>(evaluator);
    }

    [Fact]
    public void SceneImage_ModelConfiguration_ContainsBothTurnIdIndexAndFingerprintUniqueIndex()
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new CoreDbContext(options);
        var entityType = db.Model.FindEntityType(typeof(SceneImage));
        Assert.NotNull(entityType);

        var indexes = entityType.GetIndexes().ToList();

        // Assert TurnId index exists
        var turnIdIndex = indexes.FirstOrDefault(idx => idx.Properties.Count == 1 && idx.Properties[0].Name == nameof(SceneImage.TurnId));
        Assert.NotNull(turnIdIndex);

        // Assert GenerationFingerprint unique index exists
        var fpIndex = indexes.FirstOrDefault(idx => idx.Properties.Count == 1 && idx.Properties[0].Name == nameof(SceneImage.GenerationFingerprint));
        Assert.NotNull(fpIndex);
        Assert.True(fpIndex.IsUnique);
    }

    [Fact]
    public void ImageGenerationJob_ModelConfiguration_ContainsAcceptedAttemptIdAndIndexes()
    {
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var db = new CoreDbContext(options);
        var entityType = db.Model.FindEntityType(typeof(ImageGenerationJob));
        Assert.NotNull(entityType);

        var indexes = entityType.GetIndexes().ToList();
        var acceptedIndex = indexes.FirstOrDefault(idx => idx.Properties.Count == 1 && idx.Properties[0].Name == nameof(ImageGenerationJob.AcceptedAttemptId));
        Assert.NotNull(acceptedIndex);
    }
}
