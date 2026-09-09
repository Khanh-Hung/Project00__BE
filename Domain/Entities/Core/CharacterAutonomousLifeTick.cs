using System;

namespace Domain.Entities;

public enum AutonomousTickState
{
    InProgress = 1,
    Completed = 2,
    Failed = 3
}

/// <summary>
/// Authoritative domain aggregate tracking factual autonomous life loop tick executions.
/// Enforces durable database-level idempotency via unique constraint on (CharacterId, SimulationTickId).
/// Strictly decouples SimulationTickId from CycleId, ExecutionId, and EventId.
/// </summary>
public sealed class CharacterAutonomousLifeTick
{
    public Guid Id { get; private set; }
    public Guid CharacterId { get; private set; }
    public Guid SimulationTickId { get; private set; }
    public DateTimeOffset SimulationTimeUtc { get; private set; }
    public Guid CycleId { get; private set; }
    public Guid ExecutionId { get; private set; }
    public Guid EventId { get; private set; }
    public AutonomousTickState State { get; private set; }
    public int? TerminalStatus { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public string? FailureReason { get; private set; }
    public uint Version { get; private set; } = 1;

    private CharacterAutonomousLifeTick() { } // EF Core

    public CharacterAutonomousLifeTick(
        Guid characterId,
        Guid simulationTickId,
        DateTimeOffset simulationTimeUtc,
        Guid cycleId,
        Guid executionId,
        Guid eventId,
        DateTimeOffset createdAtUtc,
        Guid? id = null)
    {
        if (characterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(characterId));

        if (simulationTickId == Guid.Empty)
            throw new ArgumentException("SimulationTickId cannot be empty.", nameof(simulationTickId));

        if (simulationTimeUtc == default)
            throw new ArgumentException("SimulationTimeUtc cannot be default.", nameof(simulationTimeUtc));

        if (cycleId == Guid.Empty)
            throw new ArgumentException("CycleId cannot be empty.", nameof(cycleId));

        if (executionId == Guid.Empty)
            throw new ArgumentException("ExecutionId cannot be empty.", nameof(executionId));

        if (eventId == Guid.Empty)
            throw new ArgumentException("EventId cannot be empty.", nameof(eventId));

        // Strict identity separation invariants
        if (simulationTickId == cycleId)
            throw new InvalidOperationException("SimulationTickId must be distinct from CycleId.");

        if (simulationTickId == executionId)
            throw new InvalidOperationException("SimulationTickId must be distinct from ExecutionId.");

        if (simulationTickId == eventId)
            throw new InvalidOperationException("SimulationTickId must be distinct from EventId.");

        if (cycleId == executionId)
            throw new InvalidOperationException("CycleId must be distinct from ExecutionId.");

        if (cycleId == eventId)
            throw new InvalidOperationException("CycleId must be distinct from EventId.");

        if (executionId == eventId)
            throw new InvalidOperationException("ExecutionId must be distinct from EventId.");

        Id = id ?? Guid.CreateVersion7();
        CharacterId = characterId;
        SimulationTickId = simulationTickId;
        SimulationTimeUtc = simulationTimeUtc;
        CycleId = cycleId;
        ExecutionId = executionId;
        EventId = eventId;
        State = AutonomousTickState.InProgress;
        CreatedAtUtc = createdAtUtc;
        Version = 1;
    }

    public static CharacterAutonomousLifeTick CreateClaim(
        Guid characterId,
        Guid simulationTickId,
        DateTimeOffset simulationTimeUtc,
        DateTimeOffset createdAtUtc)
    {
        var cycleId = Guid.CreateVersion7();
        var executionId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();

        while (cycleId == simulationTickId) cycleId = Guid.CreateVersion7();
        while (executionId == simulationTickId || executionId == cycleId) executionId = Guid.CreateVersion7();
        while (eventId == simulationTickId || eventId == cycleId || eventId == executionId) eventId = Guid.CreateVersion7();

        return new CharacterAutonomousLifeTick(
            characterId: characterId,
            simulationTickId: simulationTickId,
            simulationTimeUtc: simulationTimeUtc,
            cycleId: cycleId,
            executionId: executionId,
            eventId: eventId,
            createdAtUtc: createdAtUtc
        );
    }

    public void ValidatePayload(DateTimeOffset incomingSimulationTimeUtc)
    {
        if (SimulationTimeUtc != incomingSimulationTimeUtc)
        {
            throw new InvalidOperationException(
                $"Divergent payload for SimulationTickId '{SimulationTickId:D}': existing SimulationTimeUtc is {SimulationTimeUtc:O} but received {incomingSimulationTimeUtc:O}.");
        }
    }

    public void MarkCompleted(int terminalStatus, DateTimeOffset completedAtUtc)
    {
        if (State == AutonomousTickState.Completed)
            return;

        State = AutonomousTickState.Completed;
        TerminalStatus = terminalStatus;
        CompletedAtUtc = completedAtUtc;
        FailureReason = null;
        Version++;
    }

    public void MarkFailed(string reason)
    {
        if (State == AutonomousTickState.Completed)
            throw new InvalidOperationException($"Cannot mark an already Completed tick as Failed (SimulationTickId: {SimulationTickId:D}).");

        State = AutonomousTickState.Failed;
        FailureReason = reason;
        Version++;
    }
}
