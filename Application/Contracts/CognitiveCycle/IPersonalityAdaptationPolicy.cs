using Domain.ValueObjects;

namespace Application.Contracts.CognitiveCycle;

/// <summary>
/// Domain policy that inspects cognitive cycle context and result to determine whether
/// a meaningful behavioral outcome warrants personality adaptation evidence.
/// Invariant: Must return null for infrastructure/system failures.
/// Invariant: Must not accept caller-injected personality deltas.
/// </summary>
public interface IPersonalityAdaptationPolicy
{
    PersonalityAdaptationProposal? Evaluate(
        CharacterCognitiveCycleContext context,
        CharacterCognitiveCycleResult result);
}
