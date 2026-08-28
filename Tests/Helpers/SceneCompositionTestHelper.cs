using Application.Interfaces;
using Application.Services;
using Infrastructure.Persistence;
using Infrastructure.Services.Scene;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests
{
    public static class SceneCompositionTestHelper
    {
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
        public static ISceneCompositionPipelineService CreatePipeline(CoreDbContext db) => Tests.SceneCompositionTestHelper.CreatePipeline(db);
    }
}
