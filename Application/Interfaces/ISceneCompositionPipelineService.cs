using Application.DTOs;
using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Interfaces;

public sealed record SceneCompositionPipelineResult(
    SceneSpecification SceneSpecification,
    VisualContextResolutionResult VisualContext,
    ScenePrompt ScenePrompt,
    VisualSnapshot VisualSnapshot
);

public interface ISceneCompositionPipelineService
{
    Task<SceneCompositionPipelineResult> ExecuteAsync(
        SceneIntent intent,
        GenerationProfile generationProfile,
        int sceneRevision = 1,
        CancellationToken ct = default);
}
