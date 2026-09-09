using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.Autonomous;

namespace Application.Interfaces;

/// <summary>
/// Orchestrates a single deterministic autonomous life loop cycle.
/// Enforces strict at-most-once execution per (CharacterId, SimulationTickId).
/// <para>
/// Crash &amp; Recovery Invariant (PR56 MVP):
/// An InProgress claim in the database ledger is treated as terminal-for-recovery to prevent
/// split-brain dual executions without distributed fencing tokens.
/// In the event of a worker crash, retrying with the same SimulationTickId will recognize
/// the existing claim and short-circuit. Distributed lease reconciliation and recovery are handled in PR58.
/// </para>
/// </summary>
public interface IAutonomousCharacterService
{
    /// <summary>
    /// Executes a single autonomous cognitive cycle for the given character and simulation tick.
    /// </summary>
    /// <param name="characterId">Authoritative character ID.</param>
    /// <param name="simulationTickId">Authoritative external simulation tick ID.</param>
    /// <param name="simulationTimeUtc">Authoritative simulation timestamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Autonomous cycle result detailing outcome and domain evaluations.</returns>
    Task<AutonomousCycleResult> RunOnceAsync(
        Guid characterId,
        Guid simulationTickId,
        DateTimeOffset simulationTimeUtc,
        CancellationToken cancellationToken = default);
}
