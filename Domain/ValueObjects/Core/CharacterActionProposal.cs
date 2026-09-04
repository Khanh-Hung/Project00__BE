using System;
using Domain.Enums;

namespace Domain.ValueObjects;

public sealed record CharacterActionProposal
{
    public ActionType Type { get; init; }
    public double Intensity { get; init; }
    public IntentType SourceIntent { get; init; }
    public double Motivation { get; init; }
    public int StateVersion { get; init; }

    public CharacterActionProposal(
        ActionType type,
        double intensity,
        IntentType sourceIntent,
        double motivation,
        int stateVersion)
    {
        if (double.IsNaN(intensity) || double.IsInfinity(intensity))
        {
            throw new ArgumentException("Action proposal intensity must be a finite real number.", nameof(intensity));
        }

        if (intensity < 0.0 || intensity > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(intensity), intensity, "Action proposal intensity must be bounded in [0.0, 1.0].");
        }

        if (double.IsNaN(motivation) || double.IsInfinity(motivation))
        {
            throw new ArgumentException("Action proposal motivation must be a finite real number.", nameof(motivation));
        }

        if (motivation < 0.0 || motivation > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(motivation), motivation, "Action proposal motivation must be bounded in [0.0, 1.0].");
        }

        if (stateVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stateVersion), stateVersion, "StateVersion cannot be negative.");
        }

        Type = type;
        Intensity = intensity;
        SourceIntent = sourceIntent;
        Motivation = motivation;
        StateVersion = stateVersion;
    }
}
