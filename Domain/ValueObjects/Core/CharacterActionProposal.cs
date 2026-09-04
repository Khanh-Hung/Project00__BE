using System;
using Domain.Enums;

namespace Domain.ValueObjects;

public sealed record CharacterActionProposal
{
    public ActionType Type { get; init; }
    public double Intensity { get; init; }
    public IntentType SourceIntent { get; init; }
    public MotivationType Motivation { get; init; }
    public int StateVersion { get; init; }

    public CharacterActionProposal(
        ActionType type,
        double intensity,
        IntentType sourceIntent,
        MotivationType motivation,
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
