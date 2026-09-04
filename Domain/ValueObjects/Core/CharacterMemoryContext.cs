using System;
using System.Collections.Generic;

namespace Domain.ValueObjects;

/// <summary>
/// Immutable value object representing the contextual memory envelope passed into perception and cognition.
/// Carries relevant historical memories without exposing EF entities or mutable state.
/// </summary>
public sealed record CharacterMemoryContext(
    IReadOnlyList<CharacterMemoryItem> RelevantMemories
)
{
    public static readonly CharacterMemoryContext Empty = new(Array.Empty<CharacterMemoryItem>());
}
