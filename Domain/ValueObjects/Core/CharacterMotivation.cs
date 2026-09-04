using System;
using Domain.Enums;

namespace Domain.ValueObjects;

public sealed record CharacterMotivation
{
    public MotivationType Type { get; init; }
    public double Intensity { get; init; }
    public DesireSource Source { get; init; }

    public CharacterMotivation(
        MotivationType type,
        double intensity,
        DesireSource source)
    {
        if (double.IsNaN(intensity) || double.IsInfinity(intensity))
        {
            throw new ArgumentException("Motivation intensity must be a finite real number.", nameof(intensity));
        }

        if (intensity < 0.0 || intensity > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(intensity), intensity, "Motivation intensity must be bounded in [0.0, 1.0].");
        }

        Type = type;
        Intensity = intensity;
        Source = source;
    }
}
