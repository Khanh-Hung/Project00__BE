using System;
using Domain.Enums;

namespace Domain.ValueObjects;

public sealed record CharacterEmotion
{
    public EmotionType Type { get; init; }
    public double Intensity { get; init; }
    public EmotionalValence Valence { get; init; }
    public CharacterAppraisal Appraisal { get; init; }

    public CharacterEmotion(
        EmotionType type,
        double intensity,
        EmotionalValence valence,
        CharacterAppraisal appraisal)
    {
        ArgumentNullException.ThrowIfNull(appraisal, nameof(appraisal));

        if (double.IsNaN(intensity) || double.IsInfinity(intensity))
        {
            throw new ArgumentException("Emotion intensity must be a finite real number.", nameof(intensity));
        }

        if (intensity < 0.0 || intensity > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(intensity), intensity, "Emotion intensity must be bounded in [0.0, 1.0].");
        }

        Type = type;
        Intensity = intensity;
        Valence = valence;
        Appraisal = appraisal;
    }
}
