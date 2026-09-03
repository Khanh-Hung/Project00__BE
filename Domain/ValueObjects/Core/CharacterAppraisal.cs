using System;
using Domain.Enums;

namespace Domain.ValueObjects;

public sealed record CharacterAppraisal
{
    public AppraisalType Type { get; init; }
    public AppraisalPolarity Polarity { get; init; }
    public double Intensity { get; init; }
    public AppraisalSource Source { get; init; }

    public CharacterAppraisal(
        AppraisalType type,
        AppraisalPolarity polarity,
        double intensity,
        AppraisalSource source)
    {
        if (double.IsNaN(intensity) || double.IsInfinity(intensity))
        {
            throw new ArgumentException("Appraisal intensity must be a finite real number.", nameof(intensity));
        }

        if (intensity < 0.0 || intensity > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(intensity), intensity, "Appraisal intensity must be bounded in [0.0, 1.0].");
        }

        Type = type;
        Polarity = polarity;
        Intensity = intensity;
        Source = source;
    }
}
