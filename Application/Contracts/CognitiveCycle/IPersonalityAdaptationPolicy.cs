using System.Collections.Generic;
using Domain.ValueObjects;

namespace Application.Contracts.CognitiveCycle;

/// <summary>
/// Domain policy that inspects cognitive cycle context and result to determine whether
/// meaningful behavioral outcomes warrant personality adaptation evidence.
/// Invariant: Must return empty collection for infrastructure/system failures.
/// Invariant: Must not accept caller-injected personality deltas.
/// Note: Mapping relationship deltas or stress regulation outcomes to personality proposals is a domain
/// policy heuristic representing long-term dispositions, not identity equality between states and traits.
/// </summary>
public interface IPersonalityAdaptationPolicy
{
    IReadOnlyList<PersonalityAdaptationProposal> Evaluate(
        CharacterCognitiveCycleContext context,
        CharacterCognitiveCycleResult result);
}
