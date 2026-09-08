using System;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Data;
using Application.Abstractions.Time;
using Application.Contracts.CognitiveCycle;
using Application.Interfaces;
using Domain.Entities;
using Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Services.CognitiveCycle;

/// <summary>
/// Authoritative boundary for ingesting factual WorldCognitiveEvent records and safely
/// dispatching them into the Cognitive Cycle pipeline.
/// Guarantees:
/// 1. Character-scoped boundary isolation.
/// 2. Database-backed unique idempotency claim with lease-based crash recovery and failed event retries.
/// 3. Identity separation: EventId != CycleId != ExecutionId != OutboxMessageId.
/// 4. Preserves WorldCognitiveEvent.EventId for end-to-end provenance.
/// 5. Never directly mutates CharacterState, Memory, Relationship, or Personality.
/// 6. Never calls LifeSimulation directly.
/// 7. Always routes through CognitiveCycleService (and therefore Safety Gate).
/// </summary>
public sealed class WorldCognitiveEventConsumer : IWorldCognitiveEventConsumer
{
    private readonly IWorldCognitiveEventConsumptionRepository _consumptionRepository;
    private readonly ICharacterCognitiveCycleService _cognitiveCycleService;
    private readonly ISystemClock _systemClock;
    private readonly ILogger<WorldCognitiveEventConsumer> _logger;
    private readonly IWorldCognitiveEventInFlightTracker _inFlightTracker;

    public WorldCognitiveEventConsumer(
        IWorldCognitiveEventConsumptionRepository consumptionRepository,
        ICharacterCognitiveCycleService cognitiveCycleService,
        ISystemClock systemClock,
        ILogger<WorldCognitiveEventConsumer> logger,
        IWorldCognitiveEventInFlightTracker? inFlightTracker = null)
    {
        _consumptionRepository = consumptionRepository ?? throw new ArgumentNullException(nameof(consumptionRepository));
        _cognitiveCycleService = cognitiveCycleService ?? throw new ArgumentNullException(nameof(cognitiveCycleService));
        _systemClock = systemClock ?? throw new ArgumentNullException(nameof(systemClock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _inFlightTracker = inFlightTracker ?? WorldCognitiveEventInFlightTracker.Instance;
    }

    public async Task<WorldCognitiveEventConsumptionResult> ConsumeAsync(
        WorldCognitiveEvent worldEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Invariant payload and identity validation using unified ISystemClock
        ValidateWorldEvent(worldEvent, _systemClock);

        // 2. In-flight execution check: prevent duplicate dispatch while an active execution is running
        if (_inFlightTracker.IsInFlight(worldEvent.EventId))
        {
            _logger.LogInformation(
                "WorldCognitiveEvent {EventId} for Character {CharacterId} is currently being processed in-flight. Skipping duplicate execution.",
                worldEvent.EventId,
                worldEvent.CharacterId);

            var existing = await _consumptionRepository.GetByEventIdAsync(worldEvent.EventId, cancellationToken);
            return WorldCognitiveEventConsumptionResult.DuplicateResult(
                worldEvent.EventId,
                worldEvent.CharacterId,
                existing?.CycleId);
        }

        // 3. Prepare claim aggregate with canonical deterministic fingerprint
        var now = _systemClock.UtcNow.UtcDateTime;
        var claim = WorldCognitiveEventConsumption.CreateClaim(
            worldEvent.EventId,
            worldEvent.CharacterId,
            worldEvent.OccurredAtUtc,
            worldEvent.EventName,
            worldEvent.Source,
            worldEvent.Category,
            now);

        // 4. Atomically claim consumption slot (DB unique constraint on EventId with lease-based crash recovery & retry)
        var (isClaimed, existingOrNew) = await _consumptionRepository.TryClaimAsync(claim, ct: cancellationToken);

        if (!isClaimed)
        {
            _logger.LogInformation(
                "WorldCognitiveEvent {EventId} for Character {CharacterId} is duplicate or actively in-flight. Status: {State}, Existing CycleId: {CycleId}",
                worldEvent.EventId,
                worldEvent.CharacterId,
                existingOrNew.State,
                existingOrNew.CycleId);

            return WorldCognitiveEventConsumptionResult.DuplicateResult(
                worldEvent.EventId,
                worldEvent.CharacterId,
                existingOrNew.CycleId);
        }

        // 5. Register in-flight execution guard for the duration of Cognitive Cycle execution
        if (!_inFlightTracker.TryTrack(worldEvent.EventId, out var inFlightRegistration))
        {
            _logger.LogInformation(
                "WorldCognitiveEvent {EventId} for Character {CharacterId} entered in-flight execution on another worker. Returning duplicate.",
                worldEvent.EventId,
                worldEvent.CharacterId);

            return WorldCognitiveEventConsumptionResult.DuplicateResult(
                worldEvent.EventId,
                worldEvent.CharacterId,
                existingOrNew.CycleId);
        }

        using (inFlightRegistration)
        {
            var claimVersion = existingOrNew.Version;

            _logger.LogInformation(
                "WorldCognitiveEvent {EventId} claimed for Character {CharacterId} (Attempt #{AttemptCount}, ClaimVersion={ClaimVersion})",
                worldEvent.EventId,
                worldEvent.CharacterId,
                existingOrNew.AttemptCount,
                claimVersion);

            // 5. Strict identity separation: EventId != CycleId != ExecutionId
            // Architectural Contract Note:
            // The existing CharacterCognitiveCycleContext contract (PR #45/PR #52) mandates a non-empty ExecutionId
            // upon cycle invocation (CharacterCognitiveCycleService validates ExecutionId != Guid.Empty).
            // In accordance with PR54 invariants:
            // - EventId != CycleId
            // - EventId != ExecutionId
            // - CycleId != ExecutionId
            // If the Cognitive Cycle does not produce an actionable proposal, ExecutionId remains unused.
            var cycleId = Guid.NewGuid();
            while (cycleId == worldEvent.EventId)
            {
                cycleId = Guid.NewGuid();
            }

            var executionId = Guid.NewGuid();
            while (executionId == worldEvent.EventId || executionId == cycleId)
            {
                executionId = Guid.NewGuid();
            }

            var cycleContext = new CharacterCognitiveCycleContext(
                CycleId: cycleId,
                ExecutionId: executionId,
                CharacterId: worldEvent.CharacterId,
                TriggeredAtUtc: _systemClock.UtcNow,
                Event: worldEvent);

            // 6. Dispatch into authoritative Cognitive Cycle pipeline
            try
            {
                var cycleResult = await _cognitiveCycleService.RunAsync(cycleContext, cancellationToken);

                if (cycleResult.Status is CharacterCognitiveCycleStatus.Failed
                    or CharacterCognitiveCycleStatus.ConcurrencyConflict
                    or CharacterCognitiveCycleStatus.IdempotencyConflict
                    or CharacterCognitiveCycleStatus.InvalidInput)
                {
                    var failureReason = $"Cognitive cycle completed with non-success status: {cycleResult.Status}";
                    _logger.LogWarning(
                        "WorldCognitiveEvent {EventId} dispatch resulted in cycle status {Status} for Character {CharacterId}",
                        worldEvent.EventId,
                        cycleResult.Status,
                        worldEvent.CharacterId);

                    await _consumptionRepository.MarkFailedAsync(worldEvent.EventId, failureReason, _systemClock.UtcNow.UtcDateTime, claimVersion, cancellationToken);
                    return WorldCognitiveEventConsumptionResult.RejectedResult(worldEvent.EventId, worldEvent.CharacterId, failureReason);
                }

                var consumedAt = _systemClock.UtcNow.UtcDateTime;
                try
                {
                    await _consumptionRepository.MarkConsumedAsync(worldEvent.EventId, cycleResult.CycleId, consumedAt, claimVersion, cancellationToken);
                }
                catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Stale worker fenced out for WorldCognitiveEvent {EventId}. Claim was reclaimed by another worker (ClaimVersion: {ClaimVersion}). Stale cycle result dropped.",
                        worldEvent.EventId,
                        claimVersion);

                    return WorldCognitiveEventConsumptionResult.DuplicateResult(
                        worldEvent.EventId,
                        worldEvent.CharacterId,
                        cycleResult.CycleId);
                }

                _logger.LogInformation(
                    "WorldCognitiveEvent {EventId} successfully consumed by Cycle {CycleId} for Character {CharacterId}",
                    worldEvent.EventId,
                    cycleResult.CycleId,
                    worldEvent.CharacterId);

                return WorldCognitiveEventConsumptionResult.ProcessedResult(
                    worldEvent.EventId,
                    worldEvent.CharacterId,
                    cycleResult.CycleId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error executing cognitive cycle for WorldCognitiveEvent {EventId} on Character {CharacterId}",
                    worldEvent.EventId,
                    worldEvent.CharacterId);

                try
                {
                    await _consumptionRepository.MarkFailedAsync(worldEvent.EventId, ex.Message, _systemClock.UtcNow.UtcDateTime, claimVersion, CancellationToken.None);
                }
                catch (Exception markEx)
                {
                    _logger.LogCritical(
                        markEx,
                        "CRITICAL RECOVERY REQUIRED: Failed to persist failure state for WorldCognitiveEvent {EventId}. Record remains in InProgress state and will be reclaimed upon lease expiration.",
                        worldEvent.EventId);
                }

                throw;
            }
        }
    }

    public static void ValidateWorldEvent(WorldCognitiveEvent worldEvent, ISystemClock clock)
    {
        ArgumentNullException.ThrowIfNull(worldEvent);
        ArgumentNullException.ThrowIfNull(clock);

        if (worldEvent.EventId == Guid.Empty)
            throw new ArgumentException("EventId cannot be empty.", nameof(worldEvent));

        if (worldEvent.CharacterId == Guid.Empty)
            throw new ArgumentException("CharacterId cannot be empty.", nameof(worldEvent));

        if (worldEvent.OccurredAtUtc == default ||
            worldEvent.OccurredAtUtc == DateTimeOffset.MinValue ||
            worldEvent.OccurredAtUtc == DateTimeOffset.MaxValue ||
            worldEvent.OccurredAtUtc > clock.UtcNow.AddDays(1) ||
            worldEvent.OccurredAtUtc < clock.UtcNow.AddYears(-50))
        {
            throw new ArgumentException("OccurredAtUtc is invalid.", nameof(worldEvent));
        }

        if (string.IsNullOrWhiteSpace(worldEvent.Source))
            throw new ArgumentException("Source cannot be empty or whitespace.", nameof(worldEvent));

        if (string.IsNullOrWhiteSpace(worldEvent.EventName))
            throw new ArgumentException("EventName cannot be empty or whitespace.", nameof(worldEvent));
    }
}
