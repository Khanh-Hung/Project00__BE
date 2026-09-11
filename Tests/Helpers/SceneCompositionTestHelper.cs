using System.Collections.Generic;
using Application.Abstractions.Data;
using Application.Interfaces;
using Application.Services;
using Infrastructure.ImageGeneration;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Infrastructure.Services.Scene;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests
{
    public static class SceneCompositionTestHelper
    {
        public static IConfiguration CreateDefaultTestConfiguration() =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiProviders:ImageGeneration:DefaultModel"] = "meinamix",
                    ["AiProviders:ImageGeneration:DefaultWorkflow"] = "VisualIdentity",
                    ["AiProviders:ImageGeneration:StyleModels:Anime"] = "meinamix",
                    ["AiProviders:ImageGeneration:StyleModels:Manhwa"] = "meinamix",
                    ["AiProviders:ImageGeneration:StyleModels:Realistic"] = "epicrealism",
                    ["AiProviders:ImageGeneration:StyleModels:Cinematic"] = "epicrealism"
                })
                .Build();

        public static IVisualGenerationProfileProvider CreateProfileProvider(
            IConfiguration? config = null,
            IModelRegistry? modelRegistry = null) =>
            new VisualGenerationProfileProvider(
                config ?? CreateDefaultTestConfiguration(),
                modelRegistry ?? new ConfigurationModelRegistry()
            );

        public static VisualStateResolver CreateVisualStateResolver(
            IUnitOfWork unitOfWork,
            ISceneStateTrackerService? sceneStateTracker,
            ISceneCompositionPipelineService sceneCompositionPipeline,
            IVisualGenerationProfileProvider? profileProvider = null,
            ILogger<VisualStateResolver>? logger = null) =>
            new VisualStateResolver(
                unitOfWork,
                sceneStateTracker,
                profileProvider ?? CreateProfileProvider(),
                sceneCompositionPipeline,
                logger ?? NullLogger<VisualStateResolver>.Instance
            );

        public static ISceneCompositionPipelineService CreatePipeline(CoreDbContext db)
        {
            var profileReader = new CharacterVisualProfileReader(db);
            var canonicalReader = new CanonicalReferenceReader(db);
            var memoryReader = new VisualMemoryReader(db);
            var previousSceneReader = new PreviousSceneReader(db);

            var contextFactory = new SceneCompositionContextFactory(
                profileReader, canonicalReader, memoryReader, previousSceneReader,
                NullLogger<SceneCompositionContextFactory>.Instance
            );

            var stateReader = new SceneVisualStateReader(db, NullLogger<SceneVisualStateReader>.Instance);
            var continuityResolver = new VisualContinuityResolver(stateReader, NullLogger<VisualContinuityResolver>.Instance);

            var composer = new SceneComposer(NullLogger<SceneComposer>.Instance);
            var visualContextResolver = new VisualContextResolver(NullLogger<VisualContextResolver>.Instance);
            var promptComposer = new ScenePromptComposer();
            var requestMapper = new SceneGenerationRequestMapper();

            return new SceneCompositionPipelineService(
                contextFactory,
                continuityResolver,
                composer,
                visualContextResolver,
                promptComposer,
                requestMapper,
                NullLogger<SceneCompositionPipelineService>.Instance
            );
        }
    }
}

namespace Project.Tests
{
    public static class SceneCompositionTestHelper
    {
        public static IConfiguration CreateDefaultTestConfiguration() => global::Tests.SceneCompositionTestHelper.CreateDefaultTestConfiguration();
        public static IVisualGenerationProfileProvider CreateProfileProvider(IConfiguration? config = null, IModelRegistry? modelRegistry = null) =>
            global::Tests.SceneCompositionTestHelper.CreateProfileProvider(config, modelRegistry);
        public static VisualStateResolver CreateVisualStateResolver(
            IUnitOfWork unitOfWork,
            ISceneStateTrackerService? sceneStateTracker,
            ISceneCompositionPipelineService sceneCompositionPipeline,
            IVisualGenerationProfileProvider? profileProvider = null,
            ILogger<VisualStateResolver>? logger = null) =>
            global::Tests.SceneCompositionTestHelper.CreateVisualStateResolver(unitOfWork, sceneStateTracker, sceneCompositionPipeline, profileProvider, logger);
        public static ISceneCompositionPipelineService CreatePipeline(CoreDbContext db) => global::Tests.SceneCompositionTestHelper.CreatePipeline(db);
    }
}
