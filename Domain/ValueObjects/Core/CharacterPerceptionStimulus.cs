using System;
using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Immutable value object representing a normalized sensory stimulus at the perception boundary.
/// Captures sensory input attributes without defining stimulus-specific domain behavior.
/// </summary>
public sealed record CharacterPerceptionStimulus
{
    public PerceptionStimulusType Type { get; init; }
    public string Source { get; init; }
    public string Content { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; }
    public string? Category { get; init; }

    public CharacterPerceptionStimulus(
        PerceptionStimulusType type,
        string source,
        string content,
        DateTimeOffset occurredAtUtc,
        string? category = null)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Stimulus source cannot be null or whitespace.", nameof(source));
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Stimulus content cannot be null or whitespace.", nameof(content));
        if (occurredAtUtc == default)
            throw new ArgumentException("Stimulus occurred timestamp must be valid.", nameof(occurredAtUtc));

        Type = type;
        Source = source.Trim();
        Content = content.Trim();
        OccurredAtUtc = occurredAtUtc;
        Category = category?.Trim();
    }
}
