using Domain.ValueObjects;

namespace Application.Interfaces;

/// <summary>
/// Authoritative resolver for Slot 2 (Scene Continuity) predecessor reference images.
/// Follows strict 4-tier resolution priority:
/// 1. Explicit predecessor from VisualSnapshot
/// 2. Latest accepted current artifact of the session
/// 3. Character canonical reference
/// 4. No predecessor (null)
/// Invariants: Never resolves a Quarantined, Failed, Cancelled, or Cross-Session artifact.
/// </summary>
public interface IVisualPredecessorResolver
{
    Task<VisualPredecessor?> ResolveAsync(
        Guid sessionId,
        Guid turnId,
        VisualSnapshot snapshot,
        CancellationToken ct = default);
}
