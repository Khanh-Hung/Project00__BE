using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Interfaces;

public interface IVisualPromptCompiler
{
    string CompileAvatarPrompt(Character character);
    string CompileScenePrompt(Character character, SceneContext scene, CharacterRelationship? relationship = null, Slot2Context context = Slot2Context.SameScene);
    string CompileScenePrompt(VisualSnapshot snapshot);
    string CompileNegativePrompt(VisualSnapshot snapshot, string? customNegative = null);
    string CompileNegativePrompt(CharacterVisualIdentity? identity, string? customNegative = null);
}
