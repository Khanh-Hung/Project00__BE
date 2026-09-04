using System;
using Domain.Enums;

namespace Domain.ValueObjects;

public sealed record CharacterDesire
{
    public DesireType Type { get; init; }
    public double Intensity { get; init; }
    public DesireSource Source { get; init; }
    public CharacterMotivation Motivation { get; init; }

    public CharacterDesire(
        DesireType type,
        double intensity,
        DesireSource source,
        CharacterMotivation motivation)
    {
        ArgumentNullException.ThrowIfNull(motivation, nameof(motivation));

        if (double.IsNaN(intensity) || double.IsInfinity(intensity))
        {
            throw new ArgumentException("Desire intensity must be a finite real number.", nameof(intensity));
        }

        if (intensity < 0.0 || intensity > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(intensity), intensity, "Desire intensity must be bounded in [0.0, 1.0].");
        }

        Type = type;
        Intensity = intensity;
        Source = source;
        Motivation = motivation;
    }
}
