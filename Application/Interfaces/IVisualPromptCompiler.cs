using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Interfaces;

public interface IVisualPromptCompiler
{
    string CompileAvatarPrompt(Character character);
    string CompileScenePrompt(Character character, SceneContext scene, CharacterRelationship? relationship = null);
    string CompileScenePrompt(VisualSnapshot snapshot);
}
