using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Contracts.Autonomous;
using Application.Contracts.CognitiveCycle;
using Application.Interfaces;
using Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Autonomous;

/// <summary>
/// Orchestrates a single deterministic autonomous life loop cycle.
/// Strictly delegates cognitive evaluation and action execution to ICharacterCognitiveCycleService
/// without creating a second cognitive pipeline or bypassing safety gates.
/// Enforces durable database-level idempotency per (CharacterId, SimulationTickId) ensuring strict at-most-once execution.
/// <para>
/// Crash &amp; Recovery Boundary (PR56 MVP):
/// An InProgress claim in the database ledger is treated as terminal-for-recovery.
/// In the event of an ungraceful crash or process termination, any subsequent retry for the same tick
/// is recognized as already claimed to prevent split-brain dual executions without distributed fencing leases.
/// Automated crash recovery and fencing tokens are handled in PR58.
/// </para>
/// </summary>
public sealed class AutonomousCharacterService : IAutonomousCharacterService
{
    private readonly ICharacterCognitiveCycleService _cognitiveCycleService;
    private readonly ICharacterAutonomousLifeTickRepository _tickRepository;
    private readonly ILogger<AutonomousCharacterService> _logger;

    public AutonomousCharacterService(
        ICharacterCognitiveCycleService cognitiveCycleService,
        ICharacterAutonomousLifeTickRepository tickRepository,
        ILogger<AutonomousCharacterService> logger)
    {
        _cognitiveCycleService = cognitiveCycleService ?? throw new ArgumentNullException(nameof(cognitiveCycleService));
        _tickRepository = tickRepository ?? throw new ArgumentNullException(nameof(tickRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AutonomousCycleResult> RunOnceAsync(
        Guid characterId,
        Guid simulationTickId,
        DateTimeOffset simulationTimeUtc,
        CancellationToken cancellationToken = default)
    {
        if (characterId == Guid.Empty)
        {
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));
        }

        if (simulationTickId == Guid.Empty)
        {
            throw new ArgumentException("SimulationTickId cannot be empty.", nameof(simulationTickId));
        }

        if (simulationTimeUtc == default)
        {
            throw new ArgumentException("SimulationTimeUtc must be a valid non-default timestamp.", nameof(simulationTimeUtc));
        }

        // 1. Attempt to claim tick slot atomically in DB (idempotency boundary)
        var candidateClaim = CharacterAutonomousLifeTick.CreateClaim(
            characterId,
            simulationTickId,
            simulationTimeUtc,
            simulationTimeUtc);

        var (isClaimed, authoritativeTick) = await _tickRepository.TryClaimAsync(candidateClaim, cancellationToken);

        if (!isClaimed)
        {
            _logger.LogInformation(
                "[AutonomousCharacterService] SimulationTickId {SimulationTickId} for CharacterId {CharacterId} already claimed or completed. State: {State}, CycleId: {CycleId}",
                simulationTickId, characterId, authoritativeTick.State, authoritativeTick.CycleId);

            var terminalStatus = authoritativeTick.TerminalStatus.HasValue
                ? (AutonomousCycleStatus)authoritativeTick.TerminalStatus.Value
                : (authoritativeTick.State == AutonomousTickState.Failed
                    ? AutonomousCycleStatus.Failed
                    : AutonomousCycleStatus.Executed);

            var message = authoritativeTick.State switch
            {
                AutonomousTickState.Completed => $"SimulationTick '{simulationTickId:D}' already completed.",
                AutonomousTickState.Failed => $"SimulationTick '{simulationTickId:D}' previously failed: {authoritativeTick.FailureReason ?? "Unknown error"}.",
                _ => $"SimulationTick '{simulationTickId:D}' is already claimed or in-progress (State: {authoritativeTick.State}). In PR56 MVP boundary, in-progress claims are terminal-for-recovery to prevent dual execution; recovery is deferred to PR58."
            };

            return AutonomousCycleResult.DuplicateOrAlreadyExecuted(
                characterId,
                simulationTickId,
                authoritativeTick.CycleId,
                simulationTimeUtc,
                terminalStatus,
                message: message
            );
        }

        // 2. Strict identity decoupling: SimulationTickId != CycleId != ExecutionId != EventId
        var cognitiveEvent = new AutonomousCognitiveEvent(
            EventId: authoritativeTick.EventId,
            CharacterId: characterId,
            SimulationTickId: simulationTickId,
            OccurredAtUtc: simulationTimeUtc,
            Source: "Autonomous",
            EventName: "AutonomousTick"
        );

        var cycleContext = new CharacterCognitiveCycleContext(
            CycleId: authoritativeTick.CycleId,
            ExecutionId: authoritativeTick.ExecutionId,
            CharacterId: characterId,
            TriggeredAtUtc: simulationTimeUtc,
            Event: cognitiveEvent
        );

        _logger.LogInformation(
            "[AutonomousCharacterService] Triggering autonomous cycle. CharacterId={CharacterId}, SimulationTickId={SimulationTickId}, CycleId={CycleId}, ExecutionId={ExecutionId}, EventId={EventId}, SimulationTimeUtc={SimulationTimeUtc}",
            characterId, simulationTickId, authoritativeTick.CycleId, authoritativeTick.ExecutionId, authoritativeTick.EventId, simulationTimeUtc);

        CharacterCognitiveCycleResult cycleResult;
        try
        {
            cycleResult = await _cognitiveCycleService.RunAsync(cycleContext, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[AutonomousCharacterService] Unhandled exception during cognitive cycle execution before completion for CharacterId={CharacterId}, SimulationTickId={SimulationTickId}.",
                characterId, simulationTickId);

            try
            {
                authoritativeTick.MarkFailed(ex.Message);
                await _tickRepository.UpdateAsync(authoritativeTick, CancellationToken.None);
            }
            catch (Exception updateEx)
            {
                _logger.LogWarning(updateEx, "Failed to update tick status to Failed in DB.");
            }

            return AutonomousCycleResult.FailedResult(
                characterId,
                simulationTickId,
                authoritativeTick.CycleId,
                simulationTimeUtc,
                ex.Message
            );
        }

        var result = AutonomousCycleResult.FromCognitiveCycleResult(
            characterId,
            simulationTickId,
            authoritativeTick.CycleId,
            simulationTimeUtc,
            cycleResult
        );

        // 3. Mark tick completed in DB with terminal status
        try
        {
            authoritativeTick.MarkCompleted((int)result.Status, simulationTimeUtc);
            await _tickRepository.UpdateAsync(authoritativeTick, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[AutonomousCharacterService] Failed to record tick completion in DB for SimulationTickId={SimulationTickId}. Cycle execution remains committed.",
                simulationTickId);
        }

        return result;
    }
}
