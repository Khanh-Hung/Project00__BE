using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;

namespace Application.Abstractions.Data;

public interface ICharacterLifeActivityRepository
{
    Task<CharacterLifeActivity?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<CharacterLifeActivity?> GetActiveActivityAsync(Guid characterId, CancellationToken ct = default);
    Task<IReadOnlyList<CharacterLifeActivity>> GetActiveActivitiesAsync(Guid characterId, CancellationToken ct = default);
    Task<CharacterLifeActivity?> GetNextScheduledActivityAsync(Guid characterId, DateTime asOfUtc, CancellationToken ct = default);
    Task<IReadOnlyList<CharacterLifeActivity>> GetScheduledActivitiesAsync(Guid characterId, CancellationToken ct = default);
    Task<IReadOnlyList<CharacterLifeActivity>> GetActivitiesInTimeRangeAsync(Guid characterId, DateTime startUtc, DateTime endUtc, CancellationToken ct = default);
    Task<bool> HasOverlappingActivityAsync(Guid characterId, DateTime startAtUtc, DateTime plannedEndAtUtc, Guid? excludeActivityId = null, CancellationToken ct = default);
    Task<CharacterLifeActivity?> GetOverlappingActivityAsync(Guid characterId, DateTime startAtUtc, DateTime plannedEndAtUtc, Guid? excludeActivityId = null, CancellationToken ct = default);
    Task AddAsync(CharacterLifeActivity activity, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
    void ClearTracking();
}
