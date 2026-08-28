using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Services.Scene;

public sealed class VisualMemoryReader : IVisualMemoryReader
{
    private readonly CoreDbContext _dbContext;

    public VisualMemoryReader(CoreDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<CharacterVisualMemory>> GetRelevantMemoriesAsync(
        Guid characterId,
        string? locationContext = null,
        int maxResults = 3,
        CancellationToken ct = default)
    {
        // Enforce Artifact Lifecycle & Temporal Validity Invariant:
        // 1. Only memories linked to usable artifacts (Current or Historical) are eligible. Quarantined and Deleted are excluded.
        // 2. Only active valid memories (ValidUntilTurnId == null) are eligible; superseded/invalidated memories are strictly excluded.
        var query = from memory in _dbContext.CharacterVisualMemories.AsNoTracking()
                    join img in _dbContext.SceneImages.AsNoTracking() on memory.ArtifactId equals img.Id
                    where memory.CharacterId == characterId
                          && memory.ValidUntilTurnId == null
                          && img.LifecycleStatus != ArtifactLifecycleStatus.Quarantined
                          && img.LifecycleStatus != ArtifactLifecycleStatus.Deleted
                    select memory;

        if (!string.IsNullOrWhiteSpace(locationContext))
        {
            var loc = locationContext.Trim();
            query = query.OrderByDescending(m => m.Context != null && m.Context.Contains(loc))
                .ThenByDescending(m => m.IdentityScore ?? 0.5f)
                .ThenByDescending(m => m.SceneRevision);
        }
        else
        {
            query = query.OrderByDescending(m => m.IdentityScore ?? 0.5f)
                .ThenByDescending(m => m.SceneRevision);
        }

        return await query.Take(maxResults).ToListAsync(ct);
    }

    public async Task<CharacterVisualMemory?> GetLatestMemoryAsync(
        Guid characterId,
        CancellationToken ct = default)
    {
        return await (from memory in _dbContext.CharacterVisualMemories.AsNoTracking()
                      join img in _dbContext.SceneImages.AsNoTracking() on memory.ArtifactId equals img.Id
                      where memory.CharacterId == characterId
                            && memory.ValidUntilTurnId == null
                            && img.LifecycleStatus != ArtifactLifecycleStatus.Quarantined
                            && img.LifecycleStatus != ArtifactLifecycleStatus.Deleted
                      orderby memory.CreatedAt descending
                      select memory).FirstOrDefaultAsync(ct);
    }
}
