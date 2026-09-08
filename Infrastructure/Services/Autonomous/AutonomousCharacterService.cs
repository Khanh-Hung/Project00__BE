using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Contracts.Autonomous;
using Application.Contracts.CognitiveCycle;
using Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.Autonomous;

/// <summary>
/// Orchestrates a single deterministic autonomous life loop cycle.
/// Strictly delegates cognitive evaluation and action execution to ICharacterCognitiveCycleService
/// without creating a second cognitive pipeline or bypassing safety gates.
/// </summary>
public sealed class AutonomousCharacterService : IAutonomousCharacterService
{
    private readonly ICharacterCognitiveCycleService _cognitiveCycleService;
    private readonly ILogger<AutonomousCharacterService> _logger;

    public AutonomousCharacterService(
        ICharacterCognitiveCycleService cognitiveCycleService,
        ILogger<AutonomousCharacterService> logger)
    {
        _cognitiveCycleService = cognitiveCycleService ?? throw new ArgumentNullException(nameof(cognitiveCycleService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AutonomousCycleResult> RunOnceAsync(
        Guid characterId,
        DateTimeOffset simulationTimeUtc,
        CancellationToken cancellationToken = default)
    {
        if (characterId == Guid.Empty)
        {
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));
        }

        if (simulationTimeUtc == default)
        {
            throw new ArgumentException("SimulationTimeUtc must be a valid non-default timestamp.", nameof(simulationTimeUtc));
        }

        // Distinct identity invariants: CycleId != ExecutionId != EventId
        var cycleId = Guid.CreateVersion7();
        var executionId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();

        var cognitiveEvent = new AutonomousCognitiveEvent(
            EventId: eventId,
            CharacterId: characterId,
            OccurredAtUtc: simulationTimeUtc,
            Source: "Autonomous",
            EventName: "AutonomousTick"
        );

        var cycleContext = new CharacterCognitiveCycleContext(
            CycleId: cycleId,
            ExecutionId: executionId,
            CharacterId: characterId,
            TriggeredAtUtc: simulationTimeUtc,
            Event: cognitiveEvent
        );

        _logger.LogInformation(
            "[AutonomousCharacterService] Triggering autonomous cycle. CharacterId={CharacterId}, CycleId={CycleId}, ExecutionId={ExecutionId}, EventId={EventId}, SimulationTimeUtc={SimulationTimeUtc}",
            characterId, cycleId, executionId, eventId, simulationTimeUtc);

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
                "[AutonomousCharacterService] Unhandled exception during autonomous cognitive cycle for CharacterId={CharacterId}, CycleId={CycleId}.",
                characterId, cycleId);

            return AutonomousCycleResult.FailedResult(
                characterId,
                cycleId,
                simulationTimeUtc,
                ex.Message
            );
        }

        return AutonomousCycleResult.FromCognitiveCycleResult(
            characterId,
            cycleId,
            simulationTimeUtc,
            cycleResult
        );
    }
}
