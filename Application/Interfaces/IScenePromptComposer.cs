using Application.DTOs;
using Domain.Entities;

namespace Application.Interfaces;

public interface IScenePromptComposer
{
    ScenePrompt ComposePrompt(
        SceneSpecification scene,
        VisualContextResolutionResult visualContext);
}
