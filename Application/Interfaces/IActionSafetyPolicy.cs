using Application.Contracts.Safety;
using Domain.ValueObjects;

namespace Application.Interfaces;

public interface IActionSafetyPolicy
{
    int Priority { get; }
    SafetyDecision Evaluate(CharacterActionProposal proposal, CharacterSafetyContext context);
}
