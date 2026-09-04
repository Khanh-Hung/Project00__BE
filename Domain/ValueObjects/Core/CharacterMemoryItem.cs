using System;
using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// Immutable value object representing a normalized retrieved memory item at the cognition boundary.
/// Decoupled from EF Core entity tracking and persistence concerns.
/// </summary>
public sealed record CharacterMemoryItem
{
    public Guid MemoryId { get; init; }
    public MemoryType Type { get; init; }
    public string Content { get; init; }
    public int Importance { get; init; }
    public DateTimeOffset OccurredAtUtc { get; init; }

    public CharacterMemoryItem(
        Guid memoryId,
        MemoryType type,
        string content,
        int importance,
        DateTimeOffset occurredAtUtc)
    {
        if (memoryId == Guid.Empty)
            throw new ArgumentException("MemoryId cannot be empty.", nameof(memoryId));
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Memory content cannot be empty.", nameof(content));
        if (importance is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(importance), "Importance must be between 1 and 5.");
        if (occurredAtUtc == default)
            throw new ArgumentException("OccurredAtUtc must be a valid timestamp.", nameof(occurredAtUtc));

        MemoryId = memoryId;
        Type = type;
        Content = content.Trim();
        Importance = importance;
        OccurredAtUtc = occurredAtUtc;
    }
}
