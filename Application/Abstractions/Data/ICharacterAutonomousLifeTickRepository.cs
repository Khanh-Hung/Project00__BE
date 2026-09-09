using System;
using System.Threading;
using System.Threading.Tasks;
using Domain.Entities;

namespace Application.Abstractions.Data;

public interface ICharacterAutonomousLifeTickRepository
{
    Task<CharacterAutonomousLifeTick?> GetByTickIdAsync(
        Guid characterId,
        Guid simulationTickId,
        CancellationToken ct = default);

    Task<(bool IsClaimed, CharacterAutonomousLifeTick Tick)> TryClaimAsync(
        CharacterAutonomousLifeTick candidate,
        CancellationToken ct = default);

    Task UpdateAsync(
        CharacterAutonomousLifeTick tick,
        CancellationToken ct = default);
}
