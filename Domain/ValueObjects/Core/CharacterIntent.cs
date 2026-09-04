using System;
using Domain.Enums;

namespace Domain.ValueObjects;

public sealed record CharacterIntent
{
    public IntentType Type { get; init; }
    public double Intensity { get; init; }
    public DesireType SourceDesire { get; init; }
    public MotivationType Motivation { get; init; }
    public int StateVersion { get; init; }

    public CharacterIntent(
        IntentType type,
        double intensity,
        DesireType sourceDesire,
        MotivationType motivation,
        int stateVersion)
    {
        if (double.IsNaN(intensity) || double.IsInfinity(intensity))
        {
            throw new ArgumentException("Intent intensity must be a finite real number.", nameof(intensity));
        }

        if (intensity < 0.0 || intensity > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(intensity), intensity, "Intent intensity must be bounded in [0.0, 1.0].");
        }

        if (stateVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stateVersion), stateVersion, "StateVersion cannot be negative.");
        }

        Type = type;
        Intensity = intensity;
        SourceDesire = sourceDesire;
        Motivation = motivation;
        StateVersion = stateVersion;
    }
}
