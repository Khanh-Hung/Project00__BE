using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Interfaces;

public interface IVisualGenerationProfileProvider
{
    GenerationProfile ResolveProfile(
        Character character,
        string? workflowOverride = null,
        bool isTransition = false,
        bool isColdStart = false);
}
