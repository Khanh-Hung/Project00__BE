using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.Autonomous;

namespace Application.Interfaces;

public interface IAutonomousCharacterService
{
    Task<AutonomousCycleResult> RunOnceAsync(
        Guid characterId,
        Guid simulationTickId,
        DateTimeOffset simulationTimeUtc,
        CancellationToken cancellationToken = default);
}
