using System;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Policies;

/// <summary>
/// Pure, deterministic domain policy that maps a CharacterActionProposal
/// into a discrete CharacterStateDelta.
/// Zero side-effects, zero DB, zero random, zero clock.
/// </summary>
public sealed class CharacterActionExecutionPolicy : ICharacterActionExecutionPolicy
{
    public const double EatHungerMultiplier = -30.0;
    public const double RestEnergyMultiplier = 30.0;
    public const double ReduceStressMultiplier = -25.0;
    public const double SocializeSocialNeedMultiplier = -25.0;
    public const double SeekComfortMultiplier = 25.0;
    public const double SeekSafetyStressMultiplier = -20.0;

    public CharacterStateDelta CalculateDelta(CharacterActionProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal, nameof(proposal));

        return proposal.Type switch
        {
            ActionType.Eat => CharacterStateDelta.Create(hungerDelta: EatHungerMultiplier * proposal.Intensity),
            ActionType.Rest => CharacterStateDelta.Create(energyDelta: RestEnergyMultiplier * proposal.Intensity),
            ActionType.ReduceStress => CharacterStateDelta.Create(stressDelta: ReduceStressMultiplier * proposal.Intensity),
            ActionType.Socialize => CharacterStateDelta.Create(socialNeedDelta: SocializeSocialNeedMultiplier * proposal.Intensity),
            ActionType.SeekComfort => CharacterStateDelta.Create(comfortDelta: SeekComfortMultiplier * proposal.Intensity),
            ActionType.SeekSafety => CharacterStateDelta.Create(stressDelta: SeekSafetyStressMultiplier * proposal.Intensity),
            _ => throw new ArgumentOutOfRangeException(nameof(proposal), proposal.Type, "Unsupported action type for execution.")
        };
    }
}
