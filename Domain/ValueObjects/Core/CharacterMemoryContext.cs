using System;
using System.Collections.Generic;

namespace Domain.ValueObjects;

/// <summary>
/// Immutable value object representing the contextual memory envelope passed into perception and cognition.
/// Carries relevant historical memories without exposing EF entities or mutable state.
/// </summary>
public sealed record CharacterMemoryContext
{
    public IReadOnlyList<CharacterMemoryItem> RelevantMemories { get; init; }

    public CharacterMemoryContext(IEnumerable<CharacterMemoryItem>? relevantMemories = null)
    {
        RelevantMemories = relevantMemories != null
            ? new List<CharacterMemoryItem>(relevantMemories).AsReadOnly()
            : Array.Empty<CharacterMemoryItem>();
    }

    public static readonly CharacterMemoryContext Empty = new();
}
