using System;
using Application.Contracts.Safety;
using Application.Interfaces;
using Domain.Enums;
using Domain.ValueObjects;

namespace Infrastructure.Services.Safety;

public sealed class DefaultActionSafetyPolicy : IActionSafetyPolicy
{
    public int Priority => 0;

    public SafetyDecision Evaluate(CharacterActionProposal proposal, CharacterSafetyContext context)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);

        // 1. Intensity must be a finite real number within [0.0, 1.0]
        if (double.IsNaN(proposal.Intensity) || double.IsInfinity(proposal.Intensity))
        {
            return SafetyDecision.Denied("INVALID_ACTION_INTENSITY", "Action proposal intensity must be a finite real number.");
        }

        if (proposal.Intensity < 0.0 || proposal.Intensity > 1.0)
        {
            return SafetyDecision.Denied("INVALID_ACTION_INTENSITY", $"Action proposal intensity {proposal.Intensity} is out of bounds [0.0, 1.0].");
        }

        // 2. ActionType must be a defined enum value
        if (!Enum.IsDefined(typeof(ActionType), proposal.Type))
        {
            return SafetyDecision.Denied("INVALID_ACTION_TYPE", $"Action type '{(int)proposal.Type}' is undefined.");
        }

        // 3. StateVersion consistency: proposal's StateVersion must match authoritative CharacterState.Version
        if (context.CharacterState != null && proposal.StateVersion != context.CharacterState.Version)
        {
            return SafetyDecision.Denied(
                "STATE_VERSION_MISMATCH",
                $"Action proposal state version {proposal.StateVersion} does not match authoritative state version {context.CharacterState.Version}.");
        }

        return SafetyDecision.Allowed();
    }
}
